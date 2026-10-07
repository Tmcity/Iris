using System.Net;
using System.Text.Json;
using Iris.Core.Common;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using RichardSzalay.MockHttp;
using Shouldly;
using static Iris.Providers.Tests.Ocr.Baidu.BaiduTestScope;

namespace Iris.Providers.Tests.Ocr.Baidu;

public sealed class BaiduNamedPipelineTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ActualOcrPipeline_MixedBusinessRetriesHaveNineTransportsAndSafeLogs(bool throwTransport)
    {
        using var mock = new MockHttpMessageHandler();
        var time = new Clock(); int tokenRequests = 0, transports = 0;
        mock.When("https://example.test/gateway/v1/oauth/2.0/token*").Respond(_ =>
        {
            tokenRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = TokenValue, expires_in = 60 })) };
        });
        mock.When("https://example.test/gateway/v1/rest/2.0/ocr/v1/accurate_basic*").Respond(_ =>
        {
            int n = ++transports;
            if (n % 3 != 0)
            {
                if (throwTransport) throw new HttpRequestException(ApiKey + Secret + TokenValue);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("private-http-error") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(n == 3 ? "{\"error_code\":110,\"error_msg\":\"private-auth-error\"}" : n == 6 ? "{\"error_code\":18}" : "{\"words_result\":[{\"words\":\"synthetic result\"}]}" ) };
        });
        var services = new ServiceCollection();
        services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddFakeLogging(); });
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings());
        services.AddSingleton(settings); services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(Substitute.For<IPromptTemplateProvider>()); services.AddSingleton(Substitute.For<IPromptRenderer>());
        Iris.Providers.Tests.Http.DirectSnapshotSource.Register(services);
        services.AddIrisProviders();
        services.AddHttpClient(HttpClientNames.Ocr).ConfigurePrimaryHttpMessageHandler(() => mock);
        // Only this test's transport backoff changes; the production attempt/retry limits stay intact.
        services.ConfigureAll<HttpStandardResilienceOptions>(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.UseJitter = false; });
        await using var sp = services.BuildServiceProvider();
        var factories = sp.GetServices<IOcrProviderFactory>().ToArray(); factories.Length.ShouldBe(1);
        var provider = (IAsyncDisposable)factories.Single().Create(Profile() with { TimeoutSeconds = 120 }, new ProviderCredentials(ApiKey, Secret));
        var actual = (IOcrProvider)provider; Task<Iris.Core.Ocr.OcrResult>? task = null;
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            using var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Ocr);
            client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
            task = actual.RecognizeAsync(Input(), TestContext.Current.CancellationToken);
            await time.TimerCreated(TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromMilliseconds(500));
            (await task).Lines.Single().Text.ShouldBe("synthetic result");
            transports.ShouldBe(9); tokenRequests.ShouldBe(2);
            var logs = string.Join('\n', sp.GetRequiredService<FakeLogCollector>().GetSnapshot().Select(r =>
                r.Message + " " + r.Exception + " " + string.Join(" ", r.StructuredState?.Select(kv => kv.Value) ?? [])));
            logs.ShouldContain("accurate_basic");
            foreach (string secret in new[] { ApiKey, Secret, TokenValue, Uri.EscapeDataString(ApiKey), Uri.EscapeDataString(Secret), Uri.EscapeDataString(TokenValue), "private-http-error", "private-auth-error", "synthetic result" }) logs.ShouldNotContain(secret);
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            try { await provider.DisposeAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            if (task != null) try { await task; } catch (Exception ex) { cleanup.Add(ex); }
        }
        if (primary != null && cleanup.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }
}
