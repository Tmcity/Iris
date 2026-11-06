using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned T-040 Sandbox only.");
string outputRoot = @"C:\IrisT040Output";
if (!Directory.Exists(outputRoot)) throw new InvalidOperationException("Missing owned output mapping.");
string output = Path.Combine(outputRoot, "provider-adapter-probe-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
using var transport = new OwnedLoopbackClientFactory();
var services = new ServiceCollection();
services.AddSingleton<ISettingsService>(new ProbeSettings());
services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
services.AddIrisCore();
services.AddIrisProviders();
services.AddSingleton<IHttpClientFactory>(transport);
using var container = services.BuildServiceProvider();
var factories = container.GetServices<ITranslationProviderFactory>().ToDictionary(f => f.Kind);
var deepSeekPreset = TranslationProviderPresets.All.Single(p => p.Key == "deepseek");
string[] expectedModels = ["iris-fixture"];
var profiles = new[] {
    new TranslationProviderProfile { Id="tp-000000000001", DisplayName="OpenAI synthetic", Kind=TranslationProviderKind.OpenAICompatible, BaseUrl="http://127.0.0.1:18040/v1", Model="iris-fixture" },
    new TranslationProviderProfile { Id="tp-000000000002", DisplayName="DeepSeek preset with synthetic endpoint", Kind=deepSeekPreset.Kind, BaseUrl="http://127.0.0.1:18040/deepseek/v1", Model="iris-fixture" },
    new TranslationProviderProfile { Id="tp-000000000003", DisplayName="Anthropic synthetic", Kind=TranslationProviderKind.Anthropic, BaseUrl="http://127.0.0.1:18040/anthropic", Model="iris-fixture" }
};
var rows = new List<object>();
bool passed = true;
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
foreach (var profile in profiles)
{
    var provider = factories[profile.Kind].Create(profile, new ProviderCredentials("iris-t040-not-a-real-key"));
    string step = "configured";
    try
    {
        if (!provider.Descriptor.IsConfigured) throw new InvalidOperationException("Synthetic profile was not configured.");
        step = "models";
        var models = await ((IModelCatalogSource)provider).ListModelsAsync(budget.Token).ConfigureAwait(false);
        if (!models.SequenceEqual(expectedModels)) throw new InvalidOperationException("Synthetic model list mismatch.");
        rows.Add(new { Profile = profile.Id, Step = step, Passed = true, Models = models });
        step = "test";
        var test = await provider.TestAsync(budget.Token).ConfigureAwait(false);
        if (!test.Success) { rows.Add(new { Profile = profile.Id, Step = step, Passed = false, Code = test.Error?.Code.ToString() }); passed = false; }
        else rows.Add(new { Profile = profile.Id, Step = step, Passed = true, test.Sample });
        foreach (bool truncated in new[] { false, true })
        {
            step = truncated ? "truncated" : "stream";
            string source = truncated ? "Iris T040 truncated adapter" : "Iris T040 adapter stream";
            var chunks = new List<TranslationChunk>();
            await foreach (var chunk in provider.TranslateStreamAsync(new(source,"en","zh-Hans"), budget.Token).ConfigureAwait(false)) chunks.Add(chunk);
            string text = string.Concat(chunks.Select(c => c.Delta));
            var expectedFinish = truncated ? TranslationFinishReason.Truncated : TranslationFinishReason.Completed;
            if (text != "Iris T040 synthetic translation" || chunks[^1].FinishReason != expectedFinish)
                throw new InvalidOperationException("Synthetic stream or terminal mismatch.");
            rows.Add(new { Profile = profile.Id, Step = step, Passed = true, Text = text, Finish = chunks[^1].FinishReason.ToString() });
        }
        step = "cancel";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        bool requested = false, observed = false;
        var cancelWatch = new Stopwatch();
        try
        {
            await foreach (var chunk in provider.TranslateStreamAsync(new("Iris T040 cancel adapter","en","zh-Hans"), cancel.Token).ConfigureAwait(false))
            {
                if (!requested && chunk.Delta.Length > 0) { requested = true; cancelWatch.Start(); await cancel.CancelAsync().ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { observed = true; }
        cancelWatch.Stop();
        if (!requested || !observed) throw new InvalidOperationException("Synthetic caller cancellation was not observed.");
        rows.Add(new { Profile = profile.Id, Step = step, Passed = true, CancellationMs = cancelWatch.Elapsed.TotalMilliseconds });
    }
    catch (Exception error)
    {
        passed = false;
        rows.Add(new { Profile = profile.Id, Step = step, Passed = false, Type = error.GetType().FullName,
            InnerType = error.InnerException?.GetType().FullName, Code = (error as IrisException)?.Code.ToString() });
    }
    finally { if (provider is IDisposable disposable) disposable.Dispose(); }
}
await File.WriteAllTextAsync(Path.Combine(output,"state.json"),JsonSerializer.Serialize(new {
    Utc=DateTimeOffset.UtcNow, Passed=passed, Rows=rows, ProductOperationPerformed=false,
    SourceCommit="227b3d88938543766a27d86a94ade52f9e5b89b7", Scope="Provider interfaces against synthetic loopback only. No installed Iris process, UI, real account or production proxy pipeline was exercised."
})).ConfigureAwait(false);
return passed ? 0 : 1;

internal sealed class ProbeSettings : ISettingsService
{
    public bool IsReadOnly => true;
    public AppSettings Current { get; } = new();
    public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
    public Task LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Synthetic immutable settings only.");
    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default) => throw new NotSupportedException("No product settings writes.");
}
internal sealed class OwnedLoopbackClientFactory : IHttpClientFactory, IDisposable
{
    private readonly LoopbackGuard _guard = new();
    public HttpClient CreateClient(string name) => new(_guard, disposeHandler:false) { Timeout=Timeout.InfiniteTimeSpan };
    public void Dispose() => _guard.Dispose();
    private sealed class LoopbackGuard : DelegatingHandler
    {
        internal LoopbackGuard() : base(new SocketsHttpHandler { UseProxy=false, AllowAutoRedirect=false, ConnectTimeout=TimeSpan.FromSeconds(2) }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri=request.RequestUri ?? throw new InvalidOperationException("Missing owned fixture URI.");
            if (uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" || uri.Port != 18040)
                throw new InvalidOperationException("Fixture probe refused a non-owned endpoint.");
            return base.SendAsync(request,cancellationToken);
        }
    }
}
