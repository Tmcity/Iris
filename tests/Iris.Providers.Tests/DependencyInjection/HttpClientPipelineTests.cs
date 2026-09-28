using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Iris.Core.Common;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using NSubstitute;
using RichardSzalay.MockHttp;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.DependencyInjection;

public sealed class HttpClientPipelineTests
{
    private static ServiceCollection CreateServices(MockHttpMessageHandler mockHttp)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFakeLogging();
        });

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings);

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        services.AddSingleton(promptTemplates);

        var promptRenderer = Substitute.For<IPromptRenderer>();
        services.AddSingleton(promptRenderer);

        services.AddIrisProviders();

        services.AddHttpClient(HttpClientNames.Llm)
            .ConfigurePrimaryHttpMessageHandler(() => mockHttp);

        services.AddHttpClient(HttpClientNames.Ocr)
            .ConfigurePrimaryHttpMessageHandler(() => mockHttp);

        return services;
    }

    [Fact]
    public async Task LlmClient_500Then200_RetriesOnceAndReturns200()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect("https://example.com/llm")
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });
        mockHttp.Expect("https://example.com/llm")
            .Respond(HttpStatusCode.OK, "application/json", "{\"status\":\"ok\"}");

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);

        var response = await client.GetAsync("https://example.com/llm", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task LlmClient_500Then500_RetriesOnceAndReturns500WithoutException()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect("https://example.com/llm")
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });
        mockHttp.Expect("https://example.com/llm")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);

        var response = await client.GetAsync("https://example.com/llm", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task LlmClient_400_DoesNotRetry()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect("https://example.com/llm")
            .Respond(HttpStatusCode.BadRequest, "application/json", "{\"error\":\"bad_request\"}");

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);

        var response = await client.GetAsync("https://example.com/llm", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task LlmClient_429WithRetryAfterZero_RetriesOnce()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect("https://example.com/llm")
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });
        mockHttp.Expect("https://example.com/llm")
            .Respond(HttpStatusCode.OK, "application/json", "{\"status\":\"ok\"}");

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);

        var response = await client.GetAsync("https://example.com/llm", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task LlmClient_HttpRequestExceptionThen200_Succeeds()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect("https://example.com/llm")
            .Throw(new HttpRequestException("Network failure"));
        mockHttp.Expect("https://example.com/llm")
            .Respond(HttpStatusCode.OK, "application/json", "{\"status\":\"recovered\"}");

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);

        var response = await client.GetAsync("https://example.com/llm", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task LlmClient_PostStringContent503_ResendsIdenticalRequestBody()
    {
        var mockHttp = new MockHttpMessageHandler();
        string? firstBody = null;
        string? secondBody = null;

        mockHttp.Expect(HttpMethod.Post, "https://example.com/llm")
            .Respond(async req =>
            {
                firstBody = await req.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            });
        mockHttp.Expect(HttpMethod.Post, "https://example.com/llm")
            .Respond(async req =>
            {
                secondBody = await req.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}"),
                };
            });

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);

        var response = await client.PostAsync("https://example.com/llm", new StringContent("my-prompt-payload"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstBody.ShouldBe("my-prompt-payload");
        secondBody.ShouldBe("my-prompt-payload");
        mockHttp.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task BothClients_HaveUserAgentMatchingPattern()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://example.com/*").Respond(HttpStatusCode.OK);

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        var llmClient = factory.CreateClient(HttpClientNames.Llm);
        var ocrClient = factory.CreateClient(HttpClientNames.Ocr);

        var pattern = new Regex(@"^Iris/\d+\.\d+\.\d+");

        var llmUserAgent = llmClient.DefaultRequestHeaders.UserAgent.ToString();
        var ocrUserAgent = ocrClient.DefaultRequestHeaders.UserAgent.ToString();

        pattern.IsMatch(llmUserAgent).ShouldBeTrue($"Actual LLM User-Agent: {llmUserAgent}");
        pattern.IsMatch(ocrUserAgent).ShouldBeTrue($"Actual OCR User-Agent: {ocrUserAgent}");

        await llmClient.GetAsync("https://example.com/test-llm", TestContext.Current.CancellationToken);
        await ocrClient.GetAsync("https://example.com/test-ocr", TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BothClients_TimeoutIsInfinite()
    {
        var mockHttp = new MockHttpMessageHandler();
        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        var llmClient = factory.CreateClient(HttpClientNames.Llm);
        var ocrClient = factory.CreateClient(HttpClientNames.Ocr);

        llmClient.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        ocrClient.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Privacy_SensitiveQueryParameters_AreNotLogged()
    {
        const string secretToken = "SECRET-TOKEN-123";
        const string ocrUrl = "https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic?access_token=" + secretToken;
        const string llmUrl = "https://api.openai.com/v1/chat/completions?access_token=" + secretToken;

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.Expect(ocrUrl)
            .Respond(_ =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return res;
            });
        mockHttp.Expect(ocrUrl).Respond(HttpStatusCode.OK, "application/json", "{}");

        mockHttp.Expect(llmUrl)
            .Respond(_ =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.InternalServerError);
                res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return res;
            });
        mockHttp.Expect(llmUrl).Respond(HttpStatusCode.OK, "application/json", "{}");

        var services = CreateServices(mockHttp);
        var sp = services.BuildServiceProvider();
        var logCollector = sp.GetRequiredService<FakeLogCollector>();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        var ocrClient = factory.CreateClient(HttpClientNames.Ocr);
        var llmClient = factory.CreateClient(HttpClientNames.Llm);

        var ocrResponse = await ocrClient.GetAsync(ocrUrl, TestContext.Current.CancellationToken);
        var llmResponse = await llmClient.GetAsync(llmUrl, TestContext.Current.CancellationToken);

        ocrResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        llmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var snapshot = logCollector.GetSnapshot();
        snapshot.ShouldNotBeEmpty();

        foreach (var record in snapshot)
        {
            record.Message.ShouldNotContain(secretToken);
            if (record.StructuredState != null)
            {
                foreach (var kvp in record.StructuredState)
                {
                    kvp.Value?.ToString()?.ShouldNotContain(secretToken);
                }
            }
        }

        snapshot.Any(r =>
            r.Category?.Contains(nameof(HttpLoggingHandler)) == true &&
            r.Level == LogLevel.Debug &&
            r.Message.Contains("/rest/2.0/ocr/v1/general_basic")
        ).ShouldBeTrue();
    }

    [Fact]
    public void BuildContainer_WithValidateOnBuild_SucceedsWithoutException()
    {
        var mockHttp = new MockHttpMessageHandler();
        var services = CreateServices(mockHttp);

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        sp.ShouldNotBeNull();
    }

    [Fact]
    public void AddIrisProviders_RegistersExactlyOneOfEachFactory_EvenWhenCalledTwice()
    {
        var mockHttp = new MockHttpMessageHandler();
        var services = CreateServices(mockHttp);
        services.AddIrisProviders();

        using var sp = services.BuildServiceProvider();
        var factories = sp.GetRequiredService<System.Collections.Generic.IEnumerable<ITranslationProviderFactory>>().ToList();

        factories.Count(f => f.Kind == TranslationProviderKind.OpenAICompatible).ShouldBe(1);
        factories.Count(f => f.Kind == TranslationProviderKind.Anthropic).ShouldBe(1);
    }

    private static ServiceProvider CreateProductionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings);
        services.AddSingleton(Substitute.For<IPromptTemplateProvider>());
        services.AddSingleton(Substitute.For<IPromptRenderer>());
        services.AddIrisProviders();
        return services.BuildServiceProvider();
    }

    private static SocketsHttpHandler FindSocketsHttpHandler(HttpMessageHandler handler)
    {
        var current = handler;
        var innerHandlerProp = typeof(DelegatingHandler).GetProperty(
            "InnerHandler",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        while (current is DelegatingHandler delegating)
        {
            var next = (HttpMessageHandler?)innerHandlerProp?.GetValue(delegating);
            if (next == null)
            {
                break;
            }
            current = next;
        }

        current.ShouldBeOfType<SocketsHttpHandler>();
        return (SocketsHttpHandler)current;
    }

    [Theory]
    [InlineData(HttpClientNames.Llm)]
    [InlineData(HttpClientNames.Ocr)]
    public void ProductionNamedClients_SocketsHttpHandler_HasAllowAutoRedirectFalse(string clientName)
    {
        using var sp = CreateProductionServiceProvider();
        var factory = sp.GetRequiredService<IHttpMessageHandlerFactory>();
        using var handler = factory.CreateHandler(clientName);

        var socketsHandler = FindSocketsHttpHandler(handler);
        socketsHandler.ShouldNotBeNull();
        socketsHandler.AllowAutoRedirect.ShouldBeFalse();
        socketsHandler.UseProxy.ShouldBeTrue();
        socketsHandler.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(15));
    }
}
