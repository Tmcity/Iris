using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation.Anthropic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RichardSzalay.MockHttp;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Translation.Anthropic;

[CollectionDefinition("AnthropicTests", DisableParallelization = true)]
public class AnthropicTestCollectionDefinition
{
}

[Collection("AnthropicTests")]
public sealed class AnthropicTranslationProviderTests
{
    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Test helper accepts interface")]
    private static AnthropicTranslationProvider CreateProvider(
        HttpMessageHandler handler,
        TranslationProviderProfile? profile = null,
        ProviderCredentials? credentials = null,
        FakeTimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        RenderedPrompt? renderedPrompt = null,
        int timeoutSeconds = 30)
    {
        var prof = profile ?? new TranslationProviderProfile
        {
            Id = "test-anthropic",
            DisplayName = "Test Anthropic",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = null,
            Model = "claude-sonnet-5",
            TimeoutSeconds = timeoutSeconds,
        };

        var creds = credentials ?? new ProviderCredentials("sk-ant-test-key-12345678");

        var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(HttpClientNames.Llm).Returns(httpClient);

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        promptTemplates.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var promptRenderer = Substitute.For<IPromptRenderer>();
        promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
            .Returns(callInfo =>
            {
                if (renderedPrompt != null)
                {
                    return renderedPrompt;
                }
                var req = callInfo.Arg<ProviderTranslateRequest>();
                return new RenderedPrompt(System: "You are a professional translator.", User: $"Translate to {req.TargetLanguage}: {req.Text}");
            });

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());

        var tp = timeProvider ?? new FakeTimeProvider();
        var lf = loggerFactory ?? new LoggerFactory();
        var logger = lf.CreateLogger<AnthropicTranslationProvider>();

        return new AnthropicTranslationProvider(
            prof,
            creds,
            httpClientFactory,
            promptTemplates,
            promptRenderer,
            settings,
            tp,
            logger);
    }

    // ============================================================
    // 1. 客户端构造与请求 (Client Construction & Request)
    // ============================================================

    [Fact]
    public async Task OfficialUrl_SendsPostToExpectedEndpoint_WithCorrectHeaders()
    {
        var mockHttp = new MockHttpMessageHandler();
        HttpRequestMessage? capturedRequest = null;

        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(req =>
            {
                capturedRequest = req;
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
                return res;
            });

        var provider = CreateProvider(mockHttp);
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        capturedRequest.ShouldNotBeNull();
        capturedRequest.RequestUri!.ToString().ShouldBe("https://api.anthropic.com/v1/messages");
        capturedRequest.Headers.GetValues("x-api-key").Single().ShouldBe("sk-ant-test-key-12345678");
        capturedRequest.Headers.Contains("anthropic-version").ShouldBeTrue();
        capturedRequest.Headers.Authorization.ShouldBeNull();
        capturedRequest.Headers.Contains("anthropic-beta").ShouldBeFalse();
    }

    [Fact]
    public async Task CustomBaseUrl_SendsToConfiguredEndpoint()
    {
        var mockHttp = new MockHttpMessageHandler();
        HttpRequestMessage? capturedRequest = null;

        mockHttp.When(HttpMethod.Post, "https://proxy.example.com/anthropic/v1/messages")
            .Respond(req =>
            {
                capturedRequest = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
            });

        var profile = new TranslationProviderProfile
        {
            Id = "p-proxy",
            DisplayName = "Proxy Provider",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = "https://proxy.example.com/anthropic/",
            Model = "claude-opus-5",
        };

        var provider = CreateProvider(mockHttp, profile: profile);
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        capturedRequest.ShouldNotBeNull();
        capturedRequest.RequestUri!.ToString().ShouldBe("https://proxy.example.com/anthropic/v1/messages");
    }

    [Fact]
    public async Task LocalProxy_WithoutKey_IsConfiguredAndSendsNoApiKeyOrAuthorization()
    {
        var mockHttp = new MockHttpMessageHandler();
        HttpRequestMessage? capturedRequest = null;

        mockHttp.When(HttpMethod.Post, "http://localhost:8080/v1/messages")
            .Respond(req =>
            {
                capturedRequest = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
            });

        var profile = new TranslationProviderProfile
        {
            Id = "p-local",
            DisplayName = "Local Provider",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = "http://localhost:8080",
            Model = "claude-opus-5",
        };

        var provider = CreateProvider(mockHttp, profile: profile, credentials: new ProviderCredentials(null));
        provider.Descriptor.IsConfigured.ShouldBeTrue();

        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Headers.Contains("x-api-key").ShouldBeFalse();
        capturedRequest.Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task MaxRetries_IsZero_DoesNotRetryOn529()
    {
        var mockHttp = new MockHttpMessageHandler();
        var callCount = 0;

        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage((HttpStatusCode)529)
                    {
                        Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
            });

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task RequestBody_SerializesCorrectly_WithExpectedFields()
    {
        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;

        mockHttp.When(HttpMethod.Post, "*")
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
            });

        var profile = new TranslationProviderProfile
        {
            Id = "p-body",
            DisplayName = "Body Test",
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus-5",
            MaxOutputTokens = 2048,
            Temperature = 0.7, // Should be ignored
            AnthropicOptions = new AnthropicOptions { Effort = "low" },
        };

        var provider = CreateProvider(mockHttp, profile: profile);
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().ShouldBe("claude-opus-5");
        root.GetProperty("max_tokens").GetInt32().ShouldBe(2048);
        root.GetProperty("system").GetString().ShouldBe("You are a professional translator.");
        root.GetProperty("messages").GetArrayLength().ShouldBe(1);
        root.GetProperty("messages")[0].GetProperty("role").GetString().ShouldBe("user");
        root.TryGetProperty("temperature", out _).ShouldBeFalse();
        root.TryGetProperty("thinking", out _).ShouldBeFalse();
        root.GetProperty("output_config").GetProperty("effort").GetString().ShouldBe("low");
        root.GetProperty("stream").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task RequestBody_DefaultsMaxTokensAndOmitsSystemAndOutputConfigWhenBlank()
    {
        var mockHttp = new MockHttpMessageHandler();
        string? capturedBody = null;

        mockHttp.When(HttpMethod.Post, "*")
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                };
            });

        var profile = new TranslationProviderProfile
        {
            Id = "p-default",
            DisplayName = "Default Test",
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus-5",
            MaxOutputTokens = null,
            AnthropicOptions = new AnthropicOptions { Effort = "" },
        };

        var provider = CreateProvider(
            mockHttp,
            profile: profile,
            renderedPrompt: new RenderedPrompt(System: "", User: "Translate hello"));

        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;

        root.GetProperty("max_tokens").GetInt32().ShouldBe(16000);
        root.TryGetProperty("system", out _).ShouldBeFalse();
        root.TryGetProperty("output_config", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("claude-opus-5", null, true, true)]
    [InlineData("claude-fable-5-1", null, true, true)]
    [InlineData("claude-opus-5", null, false, false)]
    [InlineData("claude-opus-5", "https://proxy.example.com", true, false)]
    [InlineData("claude-sonnet-5", null, true, false)]
    [InlineData("claude-haiku-4-5", null, true, false)]
    public async Task RefusalFallbackConditions_EnablesBetaOnlyWhenAllThreeConditionsMet(
        string model,
        string? baseUrl,
        bool refusalFallback,
        bool expectBeta)
    {
        var mockHttp = new MockHttpMessageHandler();
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        mockHttp.When(HttpMethod.Post, "*")
            .Respond(async req =>
            {
                capturedRequest = req;
                capturedBody = await req.Content!.ReadAsStringAsync();
                var fixture = expectBeta ? "anthropic_stream_fallback.txt" : "anthropic_stream_ok.txt";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture(fixture), Encoding.UTF8, "text/event-stream"),
                };
            });

        var profile = new TranslationProviderProfile
        {
            Id = "p-fallback",
            DisplayName = "Fallback Test",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = baseUrl,
            Model = model,
            AnthropicOptions = new AnthropicOptions { RefusalFallback = refusalFallback },
        };

        var provider = CreateProvider(mockHttp, profile: profile);
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        capturedRequest.ShouldNotBeNull();
        capturedBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(capturedBody);

        if (expectBeta)
        {
            capturedRequest.RequestUri!.Query.ShouldContain("beta=true");
            capturedRequest.Headers.GetValues("anthropic-beta").ShouldContain("server-side-fallback-2026-07-01");
            doc.RootElement.GetProperty("fallbacks").GetString().ShouldBe("default");
        }
        else
        {
            capturedRequest.Headers.Contains("anthropic-beta").ShouldBeFalse();
            doc.RootElement.TryGetProperty("fallbacks", out _).ShouldBeFalse();
        }
    }

    // ============================================================
    // 2. 环境变量隔离 (Environment Isolation - AC-4)
    // ============================================================

    [Fact]
    public async Task EnvironmentIsolation_CustomSettingsShieldEnvironmentCredentials()
    {
        const string envKey = "ENV-API-KEY-SENTINEL";
        const string envToken = "ENV-AUTH-TOKEN-SENTINEL";
        const string envBase = "https://env-base.invalid";

        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var oldToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
        var oldBase = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");

        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", envKey);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", envToken);
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", envBase);

            // 1. Official address profile
            var mockHttpOfficial = new MockHttpMessageHandler();
            HttpRequestMessage? reqOfficial = null;
            mockHttpOfficial.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                .Respond(req =>
                {
                    reqOfficial = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                    };
                });

            var officialProfile = new TranslationProviderProfile
            {
                Id = "p-env-off",
                DisplayName = "Official Env",
                Kind = TranslationProviderKind.Anthropic,
                BaseUrl = null,
                Model = "claude-opus-5",
            };
            var officialProvider = CreateProvider(
                mockHttpOfficial,
                profile: officialProfile,
                credentials: new ProviderCredentials("sk-ant-profile-key"));

            await foreach (var _ in officialProvider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }

            reqOfficial.ShouldNotBeNull();
            reqOfficial.RequestUri!.Host.ShouldBe("api.anthropic.com");
            reqOfficial.Headers.GetValues("x-api-key").Single().ShouldBe("sk-ant-profile-key");
            reqOfficial.Headers.Authorization.ShouldBeNull();

            foreach (var h in reqOfficial.Headers)
            {
                foreach (var val in h.Value)
                {
                    val.ShouldNotContain(envKey);
                    val.ShouldNotContain(envToken);
                }
            }

            // 2. Local proxy without key profile
            var mockHttpLocal = new MockHttpMessageHandler();
            HttpRequestMessage? reqLocal = null;
            mockHttpLocal.When(HttpMethod.Post, "http://localhost:8080/v1/messages")
                .Respond(req =>
                {
                    reqLocal = req;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(LoadFixture("anthropic_stream_ok.txt"), Encoding.UTF8, "text/event-stream"),
                    };
                });

            var localProfile = new TranslationProviderProfile
            {
                Id = "p-env-local",
                DisplayName = "Local Env",
                Kind = TranslationProviderKind.Anthropic,
                BaseUrl = "http://localhost:8080",
                Model = "claude-opus-5",
            };
            var localProvider = CreateProvider(
                mockHttpLocal,
                profile: localProfile,
                credentials: new ProviderCredentials(null));

            await foreach (var _ in localProvider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }

            reqLocal.ShouldNotBeNull();
            reqLocal.RequestUri!.ToString().ShouldBe("http://localhost:8080/v1/messages");
            reqLocal.Headers.Contains("x-api-key").ShouldBeFalse();
            reqLocal.Headers.Authorization.ShouldBeNull();

            foreach (var h in reqLocal.Headers)
            {
                foreach (var val in h.Value)
                {
                    val.ShouldNotContain(envKey);
                    val.ShouldNotContain(envToken);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", oldToken);
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", oldBase);
        }
    }

    // ============================================================
    // 3. 流式处理 (Streaming Scenarios)
    // ============================================================

    [Fact]
    public async Task Stream_OkFixture_YieldsTextDeltaAndSingleCompletedChunkWithUsage()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_ok.txt"));

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Count.ShouldBe(2);
        chunks[0].Delta.ShouldBe("你好");
        chunks[0].FinishReason.ShouldBeNull();

        chunks[1].Delta.ShouldBeEmpty();
        chunks[1].FinishReason.ShouldBe(TranslationFinishReason.Completed);
        chunks[1].Usage.ShouldNotBeNull();
        chunks[1].Usage!.InputTokens.ShouldBe(25);
        chunks[1].Usage!.OutputTokens.ShouldBe(5);

        chunks.Count(c => c.FinishReason != null).ShouldBe(1);
    }

    [Fact]
    public async Task Stream_ThinkingFixture_FiltersThinkingAndAccumulatesOutputTokens()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_thinking.txt"));

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var fullText = string.Concat(chunks.Select(c => c.Delta));
        fullText.ShouldBe("你好，世界");
        fullText.ShouldNotContain("think");
        fullText.ShouldNotContain("sig_mock");

        var finalChunk = chunks.Last();
        finalChunk.FinishReason.ShouldBe(TranslationFinishReason.Completed);
        finalChunk.Usage.ShouldNotBeNull();
        finalChunk.Usage!.OutputTokens.ShouldBe(42);
    }

    [Theory]
    [InlineData("anthropic_stream_max_tokens.txt", TranslationFinishReason.Truncated)]
    [InlineData("model_context_window_exceeded", TranslationFinishReason.Truncated)]
    [InlineData("stop_sequence", TranslationFinishReason.Completed)]
    [InlineData("brand_new_reason", TranslationFinishReason.Completed)]
    public async Task Stream_StopReasons_MappedToExpectedFinishReason(string fixtureOrReason, TranslationFinishReason expected)
    {
        var mockHttp = new MockHttpMessageHandler();
        string sseContent;

        if (fixtureOrReason.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            sseContent = LoadFixture(fixtureOrReason);
        }
        else
        {
            sseContent = LoadFixture("anthropic_stream_ok.txt")
                .Replace("\"stop_reason\":\"end_turn\"", $"\"stop_reason\":\"{fixtureOrReason}\"", StringComparison.Ordinal);
        }

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", sseContent);

        var provider = CreateProvider(mockHttp);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.Last().FinishReason.ShouldBe(expected);
    }

    [Fact]
    public async Task Stream_RefusalFixture_YieldsPartialDeltaThenThrowsContentRefused()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_refusal.txt"));

        var provider = CreateProvider(mockHttp);
        var yieldedDeltas = new List<string>();

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    yieldedDeltas.Add(chunk.Delta);
                }
            }
        });

        yieldedDeltas.ShouldContain("抱歉，");
        ex.Code.ShouldBe(ErrorCode.ContentRefused);
        ex.ProviderId.ShouldBe(provider.Descriptor.Id);
    }

    [Fact]
    public async Task Stream_TruncatedFixture_ThrowsInvalidResponseWithUnexpectedEndMessage()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_truncated.txt"));

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Detail.ShouldBe("响应流意外结束");
    }

    [Theory]
    [InlineData("overloaded_error", ErrorCode.ProviderUnavailable, 529, true)]
    [InlineData("rate_limit_error", ErrorCode.RateLimited, 429, true)]
    [InlineData("invalid_request_error", ErrorCode.BadRequest, 400, false)]
    [InlineData("some_future_error", ErrorCode.ProviderUnavailable, 500, true)]
    public async Task Stream_ErrorEvent_MapsToExpectedIrisException(string errType, ErrorCode expectedCode, int expectedStatus, bool expectedRetryable)
    {
        var mockHttp = new MockHttpMessageHandler();
        var sse = LoadFixture("anthropic_stream_error.txt")
            .Replace("overloaded_error", errType, StringComparison.Ordinal);

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", sse);

        var provider = CreateProvider(mockHttp);
        var deltas = new List<string>();

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    deltas.Add(chunk.Delta);
                }
            }
        });

        deltas.ShouldContain("你好");
        ex.Code.ShouldBe(expectedCode);
        ex.HttpStatus.ShouldBe(expectedStatus);
        ex.Retryable.ShouldBe(expectedRetryable);
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Stream_ErrorMessageEchoesKey_DetailDoesNotContainKey()
    {
        const string apiKey = "sk-ant-secret-12345678";
        var mockHttp = new MockHttpMessageHandler();
        var sse = $"event: error\ndata: {{\"type\":\"error\",\"error\":{{\"type\":\"overloaded_error\",\"message\":\"Server overloaded for key {apiKey}\"}}}}\n\n";

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", sse);

        var provider = CreateProvider(mockHttp, credentials: new ProviderCredentials(apiKey));

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Detail.ShouldNotBeNull();
        ex.Detail!.ShouldNotContain(apiKey);
    }

    [Fact]
    public async Task Stream_InvalidJsonEvent_ThrowsInvalidResponseWithoutAnthropicExceptionAsInner()
    {
        var mockHttp = new MockHttpMessageHandler();
        const string sse = "event: content_block_delta\ndata: not-valid-json-data\n\n";

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", sse);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.InnerException.ShouldBeNull();
        if (ex.InnerException != null)
        {
            ex.InnerException.GetType().Namespace!.ShouldNotStartWith("Anthropic.Exceptions");
        }
    }

    [Fact]
    public async Task Stream_FallbackBetaPath_YieldsBothPartsAndCompletes()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages?beta=true")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_fallback.txt"));

        var profile = new TranslationProviderProfile
        {
            Id = "p-beta",
            DisplayName = "Beta Fallback",
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus-5",
            AnthropicOptions = new AnthropicOptions { RefusalFallback = true },
        };

        var provider = CreateProvider(mockHttp, profile: profile);
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var fullText = string.Concat(chunks.Select(c => c.Delta));
        fullText.ShouldBe("第一部分第二部分");

        chunks.Last().FinishReason.ShouldBe(TranslationFinishReason.Completed);
    }

    // ============================================================
    // 4. HTTP 错误处理 (HTTP Error Handling)
    // ============================================================

    [Theory]
    [InlineData(400, ErrorCode.BadRequest, false)]
    [InlineData(401, ErrorCode.AuthenticationFailed, false)]
    [InlineData(403, ErrorCode.PermissionDenied, false)]
    [InlineData(404, ErrorCode.EndpointNotFound, false)]
    [InlineData(413, ErrorCode.InputTooLong, false)]
    [InlineData(429, ErrorCode.RateLimited, true)]
    [InlineData(500, ErrorCode.ProviderUnavailable, true)]
    [InlineData(529, ErrorCode.ProviderUnavailable, true)]
    public async Task HttpError_StatusCodes_MappedToExpectedErrorCodeAndProperties(int statusCode, ErrorCode expectedCode, bool expectedRetryable)
    {
        var mockHttp = new MockHttpMessageHandler();
        var json = $"{{\"type\":\"error\",\"error\":{{\"type\":\"api_error\",\"message\":\"Specific error message for {statusCode}\"}}}}";

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond((HttpStatusCode)statusCode, "application/json", json);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(expectedCode);
        ex.HttpStatus.ShouldBe(statusCode);
        ex.Retryable.ShouldBe(expectedRetryable);
        ex.ProviderId.ShouldBe(provider.Descriptor.Id);
        ex.Detail.ShouldNotBeNull();
        ex.Detail!.ShouldContain($"Specific error message for {statusCode}");
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task HttpError_401EchoesApiKey_DetailRedactsKey()
    {
        const string apiKey = "sk-ant-echo-key-12345678";
        var mockHttp = new MockHttpMessageHandler();
        var json = $"{{\"type\":\"error\",\"error\":{{\"type\":\"authentication_error\",\"message\":\"Invalid API Key: {apiKey}\"}}}}";

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond(HttpStatusCode.Unauthorized, "application/json", json);

        var provider = CreateProvider(mockHttp, credentials: new ProviderCredentials(apiKey));

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.AuthenticationFailed);
        ex.Detail.ShouldNotBeNull();
        ex.Detail!.ShouldNotContain(apiKey);
    }

    [Fact]
    public async Task HttpError_502HtmlResponse_MapsToProviderUnavailableWithCompressedHtmlDetail()
    {
        var mockHttp = new MockHttpMessageHandler();
        const string html = "<html><body>   Bad   Gateway   Error   </body></html>";

        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond((HttpStatusCode)502, "text/html", html);

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        ex.Detail.ShouldNotBeNull();
        ex.Detail!.ShouldContain("Bad Gateway Error");
    }

    // ============================================================
    // 5. 超时、取消、网络、资源 (Timeout, Cancellation & Resource)
    // ============================================================

    [Fact]
    public async Task Timeout_HeadersDelayedIndefinitely_ThrowsTimeoutIrisException()
    {
        var timeProvider = new FakeTimeProvider();
        var handler = new DelayingHandler(Timeout.InfiniteTimeSpan);
        var provider = CreateProvider(handler, timeProvider: timeProvider, timeoutSeconds: 5);

        var translateTask = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        await handler.Entered;
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        var ex = await Should.ThrowAsync<IrisException>(async () => await translateTask);
        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task ProgressTimeout_EventsArrivePeriodicallyUnderTimeout_CompletesSuccessfully()
    {
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("*")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var timeProvider = new FakeTimeProvider();
        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);

        var translateTask = Task.Run(async () =>
        {
            var chunks = new List<TranslationChunk>();
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("test", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
            return chunks;
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        stream.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: ping\ndata: {\"type\":\"ping\"}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"chunk0\"}}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: ping\ndata: {\"type\":\"ping\"}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"chunk1\"}}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
        await stream.WaitForReadAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        stream.Push("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":20}}\n\n");
        await stream.WaitForReadAsync();
        stream.Push("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        stream.Complete();

        var results = await translateTask;
        results.Count.ShouldBeGreaterThan(0);
        results.Last().FinishReason.ShouldBe(TranslationFinishReason.Completed);
    }

    [Fact]
    public async Task ProgressTimeout_StreamIdleExceedsTimeout_ThrowsTimeoutIrisException()
    {
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("*")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var timeProvider = new FakeTimeProvider();
        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);

        var translateTask = Task.Run(async () =>
        {
            var chunks = new List<TranslationChunk>();
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("test", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
            return chunks;
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        stream.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        await stream.WaitForReadAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var ex = await Should.ThrowAsync<IrisException>(async () => await translateTask);
        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_CallerCancelsDuringHeaderOrStream_ThrowsOperationCanceledException()
    {
        // 1. Waiting for response headers
        var delayingHandler = new DelayingHandler(Timeout.InfiniteTimeSpan);
        var providerHeaders = CreateProvider(delayingHandler);
        using var ctsHeaders = new CancellationTokenSource();
        var headersTask = Task.Run(async () =>
        {
            await foreach (var _ in providerHeaders.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), ctsHeaders.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await delayingHandler.Entered;
        ctsHeaders.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await headersTask);

        // 2. During stream reading
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var providerStream = CreateProvider(mockHttp);
        using var ctsStream = new CancellationTokenSource();

        var streamTask = Task.Run(async () =>
        {
            await foreach (var _ in providerStream.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), ctsStream.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        stream.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        await stream.WaitForReadAsync();

        ctsStream.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await streamTask);
    }

    [Fact]
    public async Task Cancellation_AndTimeoutSimultaneous_ThrowsOperationCanceledException()
    {
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond(req =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return res;
            });

        var timeProvider = new FakeTimeProvider();
        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);
        using var userCts = new CancellationTokenSource();

        var translateTask = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("test", "en", "zh-CN"), userCts.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        userCts.Cancel();
        timeProvider.Advance(TimeSpan.FromSeconds(20));

        await Should.ThrowAsync<OperationCanceledException>(async () => await translateTask);
    }

    [Fact]
    public async Task NetworkError_ThrowsHttpRequestException_MapsToNetworkUnavailable()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Throw(new HttpRequestException("Connection refused by peer"));

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Resource_ConsumerBreaksAfterFirstDelta_DisposesResponse()
    {
        var trackingContent = new TrackingHttpContent(LoadFixture("anthropic_stream_ok.txt"));
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = trackingContent });

        var provider = CreateProvider(mockHttp);

        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                break;
            }
        }

        trackingContent.IsDisposed.ShouldBeTrue();
    }

    // ============================================================
    // 6. 配置判定、TestAsync、模型列表、隐私
    // ============================================================

    [Theory]
    [InlineData(null, null, "sk-123", false)]
    [InlineData("", null, "sk-123", false)]
    [InlineData("   ", null, "sk-123", false)]
    [InlineData("claude-opus-5", "ftp://proxy.com", "sk-123", false)]
    [InlineData("claude-opus-5", null, "sk-123", true)]
    [InlineData("claude-opus-5", null, null, false)]
    [InlineData("claude-opus-5", null, "", false)]
    [InlineData("claude-opus-5", "http://localhost:8080", null, true)]
    [InlineData("claude-opus-5", "http://127.0.0.1:8080", "", true)]
    [InlineData("claude-opus-5", "https://proxy.example.com", null, false)]
    [InlineData("claude-opus-5", "https://proxy.example.com", "sk-123", true)]
    public void IsConfigured_FollowsSpecification(string? model, string? baseUrl, string? apiKey, bool expected)
    {
        var profile = new TranslationProviderProfile
        {
            Id = "p1",
            DisplayName = "Test",
            Kind = TranslationProviderKind.Anthropic,
            Model = model!,
            BaseUrl = baseUrl,
        };
        var credentials = new ProviderCredentials(apiKey);
        var mockHttp = new MockHttpMessageHandler();
        var provider = CreateProvider(mockHttp, profile: profile, credentials: credentials);

        provider.Descriptor.IsConfigured.ShouldBe(expected);
    }

    [Fact]
    public async Task UnconfiguredProvider_ThrowsNotConfiguredWithoutSendingHttp()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "p-unconf",
            DisplayName = "Unconfigured",
            Kind = TranslationProviderKind.Anthropic,
            Model = "",
        };
        var mockHttp = new MockHttpMessageHandler();
        var provider = CreateProvider(mockHttp, profile: profile);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.NotConfigured);
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);
    }

    [Fact]
    public void Descriptor_PropertiesMatchProfile()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "p-desc",
            DisplayName = "Anthropic Desc",
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus-5",
        };
        var mockHttp = new MockHttpMessageHandler();
        var provider = CreateProvider(mockHttp, profile: profile);

        provider.Descriptor.Id.ShouldBe("p-desc");
        provider.Descriptor.DisplayName.ShouldBe("Anthropic Desc");
        provider.Descriptor.KindName.ShouldBe("Anthropic");
        provider.Descriptor.Model.ShouldBe("claude-opus-5");
        provider.Descriptor.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task TestAsync_Success_ReturnsSample()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_ok.txt"));

        var provider = CreateProvider(mockHttp);
        var result = await provider.TestAsync(TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Sample.ShouldBe("你好");
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task TestAsync_401_ReturnsSuccessFalseWithAuthenticationFailed()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"Invalid Key\"}}");

        var provider = CreateProvider(mockHttp);
        var result = await provider.TestAsync(TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBe(ErrorCode.AuthenticationFailed);
    }

    [Fact]
    public async Task TestAsync_CallerCancel_ThrowsOperationCanceledException()
    {
        var delayingHandler = new DelayingHandler(Timeout.InfiniteTimeSpan);
        var provider = CreateProvider(delayingHandler);
        using var cts = new CancellationTokenSource();

        var testTask = Task.Run(async () => await provider.TestAsync(cts.Token), TestContext.Current.CancellationToken);

        await delayingHandler.Entered;
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await testTask);
    }

    [Fact]
    public async Task ListModelsAsync_MergesPagesDeduplicatesAndSorts()
    {
        var mockHttp = new MockHttpMessageHandler();
        HttpRequestMessage? capturedRequest = null;

        mockHttp.When(HttpMethod.Get, "https://api.anthropic.com/v1/models")
            .WithQueryString("")
            .Respond(req =>
            {
                capturedRequest = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoadFixture("anthropic_models_page1.json"), Encoding.UTF8, "application/json"),
                };
            });

        mockHttp.When(HttpMethod.Get, "https://api.anthropic.com/v1/models")
            .WithQueryString("after_id=claude-sonnet-5")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoadFixture("anthropic_models_page2.json"), Encoding.UTF8, "application/json"),
            });

        var provider = CreateProvider(mockHttp);
        var models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Headers.GetValues("x-api-key").Single().ShouldBe("sk-ant-test-key-12345678");
        capturedRequest.Headers.Authorization.ShouldBeNull();

        models.Count.ShouldBe(3);
        models[0].ShouldBe("claude-haiku-4-5");
        models[1].ShouldBe("claude-opus-5");
        models[2].ShouldBe("claude-sonnet-5");
    }

    [Fact]
    public async Task ListModelsAsync_401_ThrowsAuthenticationFailed()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/models")
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"Unauthorized\"}}");

        var provider = CreateProvider(mockHttp);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await provider.ListModelsAsync(TestContext.Current.CancellationToken);
        });

        ex.Code.ShouldBe(ErrorCode.AuthenticationFailed);
    }

    [Fact]
    public async Task Privacy_SensitiveInformation_IsNotLogged()
    {
        const string apiKey = "sk-ant-secret-key-99887766";
        const string rawInput = "TopSecretTranslationInput";
        const string translatedText = "你好";

        var fakeCollector = new FakeLogCollector();
        var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(new FakeLoggerProvider(fakeCollector));

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", LoadFixture("anthropic_stream_ok.txt"));

        var provider = CreateProvider(
            mockHttp,
            credentials: new ProviderCredentials(apiKey),
            loggerFactory: loggerFactory);

        // 1. Success translation
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest(rawInput, "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        // 2. 401 error with echoed key
        var mockHttp401 = new MockHttpMessageHandler();
        mockHttp401.When("https://api.anthropic.com/v1/messages")
            .Respond(HttpStatusCode.Unauthorized, "application/json", $"{{\"type\":\"error\",\"error\":{{\"type\":\"authentication_error\",\"message\":\"Invalid key: {apiKey}\"}}}}");

        var provider401 = CreateProvider(
            mockHttp401,
            credentials: new ProviderCredentials(apiKey),
            loggerFactory: loggerFactory);

        await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider401.TranslateStreamAsync(new ProviderTranslateRequest(rawInput, "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        // 3. Stream error with echoed key
        var mockHttpSseErr = new MockHttpMessageHandler();
        mockHttpSseErr.When("https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", $"event: error\ndata: {{\"type\":\"error\",\"error\":{{\"type\":\"overloaded_error\",\"message\":\"Echo key: {apiKey}\"}}}}\n\n");

        var providerSseErr = CreateProvider(
            mockHttpSseErr,
            credentials: new ProviderCredentials(apiKey),
            loggerFactory: loggerFactory);

        await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in providerSseErr.TranslateStreamAsync(new ProviderTranslateRequest(rawInput, "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        var logs = fakeCollector.GetSnapshot();
        logs.ShouldNotBeEmpty();

        foreach (var record in logs)
        {
            record.Message.ShouldNotContain(apiKey);
            record.Message.ShouldNotContain(rawInput);
            record.Message.ShouldNotContain(translatedText);

            if (record.StructuredState != null)
            {
                foreach (var kvp in record.StructuredState)
                {
                    var val = kvp.Value?.ToString();
                    val?.ShouldNotContain(apiKey);
                    val?.ShouldNotContain(rawInput);
                    val?.ShouldNotContain(translatedText);
                }
            }

            if (record.Exception != null)
            {
                record.Exception.GetType().Namespace!.ShouldNotStartWith("Anthropic.Exceptions");
            }
        }
    }

    // ---------------- Helper classes ----------------

    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayingHandler(TimeSpan delay)
        {
            _delay = delay;
        }

        public Task Entered => _entered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ControllableStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _currentOffset;
        private TaskCompletionSource _waitingToRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForReadAsync(TimeSpan? timeout = null)
        {
            TaskCompletionSource tcs;
            lock (_channel)
            {
                tcs = _waitingToRead;
            }
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }

        public void Push(string sseText)
        {
            lock (_channel)
            {
                if (_waitingToRead.Task.IsCompleted)
                {
                    _waitingToRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(sseText));
        }

        public void Complete()
        {
            _channel.Writer.Complete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                lock (_channel)
                {
                    _waitingToRead.TrySetResult();
                }

                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
                if (_channel.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _currentOffset = 0;
                }
            }

            var available = _currentChunk.Length - _currentOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
            _currentOffset += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingHttpContent : StringContent
    {
        public bool IsDisposed { get; private set; }

        public TrackingHttpContent(string content) : base(content, Encoding.UTF8, "text/event-stream")
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
        }
    }
}
