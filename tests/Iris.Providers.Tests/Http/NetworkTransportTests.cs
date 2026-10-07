using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using NSubstitute;
using Shouldly;

namespace Iris.Providers.Tests.Http;

public sealed class NetworkTransportTests
{
    private const string ProxyUrl = "http://proxy.example.test:8080/";
    private static readonly Uri Destination = new("http://origin.example.test/probe");
    private static NetworkProxySnapshot Snapshot(Guid id, int route = 0) => new(id, Destination, ProxyMode.Custom,
        new($"http://proxy{route}.example.test:8080/"), new("synthetic", "password"), ProxyRouteKind.Proxy);

    [Theory][InlineData(HttpClientNames.Llm)][InlineData(HttpClientNames.Ocr)]
    public Task NamedPipeline_AllRetriesKeepOriginalCredentials_ExistingClientUsesNextGeneration(string family) => Run(async f =>
    {
        f.Seed("A");
        f.Wire.Respond = (_, n) =>
        {
            if (n != 1) return "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
            f.Seed("B");
            return "HTTP/1.1 503 Unavailable\r\nRetry-After: 0\r\nContent-Length: 0\r\n\r\n";
        };
        using var client = f.Client(family);
        using (var request = Request()) f.Responses.Add(await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken));
        f.Wire.Auth.ToArray().ShouldBe(["synthetic-A:password-A", "synthetic-A:password-A"]);
        using (var request = Request()) f.Responses.Add(await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken));
        f.Wire.Auth.Last().ShouldBe("synthetic-B:password-B"); f.Wire.Endpoints.Count.ShouldBe(2);
        f.Responses[0].Dispose(); f.Responses[1].Dispose();
        await f.Manager.StopAsync(); f.Manager.HandlerCount.ShouldBe(0); f.Wire.Streams.All(s => s.Disposed).ShouldBeTrue();
    });

    [Fact]
    public Task LateCapturedA_CannotReversePublishedB_AndSnapshotDraftDoesNotPublish() => Run(async f =>
    {
        f.Seed("A"); var first = await f.Source.CaptureCurrentAsync(Destination, TestContext.Current.CancellationToken);
        f.Seed("B"); var second = await f.Source.CaptureCurrentAsync(Destination, TestContext.Current.CancellationToken);
        using var b = f.Manager.Acquire(HttpClientNames.Llm, second);
        using var a = f.Manager.Acquire(HttpClientNames.Llm, first);
        f.Manager.ExpectedGeneration.ShouldBe(second.GenerationId); a.Entry.ShouldNotBeSameAs(b.Entry);
        var baseline = f.Settings.Current.Network;
        await f.Source.CaptureDraftAsync(new(baseline, baseline, new(CredentialEditKind.Clear, baseline.CustomProxyUrl, baseline.ProxyCredentialSetId)), Destination, TestContext.Current.CancellationToken);
        f.Manager.ExpectedGeneration.ShouldBe(second.GenerationId);
    });

    [Theory][InlineData(HttpClientNames.Llm)][InlineData(HttpClientNames.Ocr)]
    public Task ActualSockets_PostBodySurvivesProxyAuthenticationAndBusinessRetry(string family) => Run(async f =>
    {
        f.Seed("A"); const string body = "synthetic=request-body"; var bodies = new List<string>();
        f.Wire.Respond = (request, n) =>
        {
            bodies.Add(request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
            return n == 1 ? "HTTP/1.1 503 Unavailable\r\nRetry-After: 0\r\nContent-Length: 0\r\n\r\n" : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
        };
        using var client = f.Client(family); using var request = Request(); request.Method = HttpMethod.Post; request.Content = new StringContent(body);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken); f.Responses.Add(response);
        response.StatusCode.ShouldBe(HttpStatusCode.OK); bodies.ShouldBe([body, body]);
    });

    [Fact]
    public Task Capacity_EightHeldGenerationsRefuseNinthThenRecoverWithoutOldRouteFallback() => Run(async f =>
    {
        var leases = new List<NetworkTransportManager.Lease>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var id = Guid.NewGuid(); f.Manager.Apply(new(), id); leases.Add(f.Manager.Acquire(HttpClientNames.Llm, Snapshot(id)));
            }
            var ninth = Guid.NewGuid();
            Should.Throw<IrisException>(() => f.Manager.Apply(new(), ninth)); f.Manager.ExpectedGeneration.ShouldBe(ninth);
            Should.Throw<IrisException>(() => f.Manager.Acquire(HttpClientNames.Ocr, Snapshot(ninth)));
            f.Manager.GenerationCount.ShouldBe(8);
            leases[0].Dispose(); f.Manager.Apply(new(), ninth);
            using var accepted = f.Manager.Acquire(HttpClientNames.Ocr, Snapshot(ninth)); f.Manager.GenerationCount.ShouldBe(8);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        await f.Manager.StopAsync(); f.Manager.GenerationCount.ShouldBe(0);
    });

    [Fact]
    public Task Capacity_SixteenRoutesPerFamily_LruEvictsOnlyUnheldEntries() => Run(async f =>
    {
        var id = Guid.NewGuid(); f.Manager.Apply(new(), id); var leases = new List<NetworkTransportManager.Lease>();
        try
        {
            for (int i = 0; i < 16; i++) leases.Add(f.Manager.Acquire(HttpClientNames.Llm, Snapshot(id, i)));
            Should.Throw<IrisException>(() => f.Manager.Acquire(HttpClientNames.Llm, Snapshot(id, 16)));
            using var otherFamily = f.Manager.Acquire(HttpClientNames.Ocr, Snapshot(id, 16));
            f.Manager.HandlerCount.ShouldBe(17);
            var victim = leases[1].Entry; leases[1].Dispose();
            using var replacement = f.Manager.Acquire(HttpClientNames.Llm, Snapshot(id, 16));
            victim.Retired.ShouldBeTrue(); f.Manager.HandlerCount.ShouldBe(17); leases[0].Entry.Retired.ShouldBeFalse();
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        await f.Manager.StopAsync();
    });

    [Fact]
    public Task SystemResolution_FreezesCredentialsRouteAndDefaultIdentityBeforeSockets() => Run(async f =>
    {
        f.Settings.Value = new() { Network = new() { ProxyMode = ProxyMode.System, BypassProxyOnLocal = false } };
        var original = new Uri("http://private:private@system-proxy.example.test:8080/");
        var mutable = new NetworkCredential("system-A", "password-A", "domain");
        var callbacks = new List<Uri>();
        var credentials = Substitute.For<ICredentials>();
        credentials.GetCredential(Arg.Any<Uri>(), Arg.Any<string>()).Returns(c => { callbacks.Add(c.Arg<Uri>()); return mutable; });
        var proxy = Substitute.For<IWebProxy>(); proxy.GetProxy(Arg.Any<Uri>()).Returns(original); proxy.Credentials.Returns(credentials);
        f.ResolverProxy = proxy;
        var snap = await f.Source.CaptureCurrentAsync(Destination, TestContext.Current.CancellationToken);
        using var first = f.Manager.Acquire(HttpClientNames.Llm, snap);
        var route = f.Factory.Routes.Last(); route.ProxyUri!.UserInfo.ShouldBeEmpty(); callbacks.All(u => u == original).ShouldBeTrue();
        mutable.UserName = "system-B"; mutable.Password = "password-B";
        route.CreateProxy()!.Credentials!.GetCredential(route.ProxyUri, "Basic")!.UserName.ShouldBe("system-A");
        route.CreateProxy()!.Credentials!.GetCredential(new("http://other.example.test/"), "Basic").ShouldBeNull();
        using var second = f.Manager.Acquire(HttpClientNames.Llm, snap); first.Entry.ShouldNotBeSameAs(second.Entry);
        proxy.Credentials.Returns(CredentialCache.DefaultCredentials);
        using var defaults = f.Manager.Acquire(HttpClientNames.Llm, snap);
        f.Factory.Routes.Last().UsesDefaultCredentials.ShouldBeTrue();
        f.Factory.Sockets.Last().DefaultProxyCredentials.ShouldBeSameAs(CredentialCache.DefaultCredentials);
        proxy.GetProxy(Arg.Any<Uri>()).Returns(new Uri("socks4a://system-proxy.example.test:1080/"));
        using var socks = f.Manager.Acquire(HttpClientNames.Ocr, snap); f.Factory.Routes.Last().ProxyUri!.Scheme.ShouldBe("socks4a");
    });

    [Fact]
    public Task SystemCallbackErrorsAndCallerProxyAuth_AreRejectedBeforeConnection() => Run(async f =>
    {
        f.Settings.Value = new() { Network = new() { BypassProxyOnLocal = false } };
        var proxy = Substitute.For<IWebProxy>(); proxy.GetProxy(Arg.Any<Uri>()).Returns(_ => throw new IOException("private-proxy-secret")); f.ResolverProxy = proxy;
        using var client = f.Client(HttpClientNames.Llm);
        using (var request = Request())
        {
            var error = await Should.ThrowAsync<IrisException>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken));
            error.Code.ShouldBe(ErrorCode.NetworkUnavailable); error.ToString().ShouldNotContain("private-proxy-secret");
        }
        f.Seed("A");
        using (var request = Request())
        {
            request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic synthetic-untrusted");
            (await Should.ThrowAsync<IrisException>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken))).Code.ShouldBe(ErrorCode.InvalidArgument);
        }
        f.Wire.Endpoints.ShouldBeEmpty();
    });

    [Fact]
    public Task SameGenerationLocalBypassAndSystemException_AreSeparateFromProxyPool() => Run(async f =>
    {
        f.Seed("A"); var remote = await f.Source.CaptureCurrentAsync(Destination, TestContext.Current.CancellationToken);
        var local = await f.Source.CaptureCurrentAsync(new("http://localhost/"), TestContext.Current.CancellationToken);
        local.GenerationId.ShouldBe(remote.GenerationId);
        using var proxy = f.Manager.Acquire(HttpClientNames.Llm, remote); using var direct = f.Manager.Acquire(HttpClientNames.Llm, local);
        proxy.Entry.ShouldNotBeSameAs(direct.Entry); f.Factory.Sockets.Last().UseProxy.ShouldBeFalse();
    });

    [Theory][InlineData(HttpClientNames.Llm, "eof")][InlineData(HttpClientNames.Ocr, "eof")]
    [InlineData(HttpClientNames.Llm, "cancel")][InlineData(HttpClientNames.Ocr, "cancel")]
    [InlineData(HttpClientNames.Llm, "error")][InlineData(HttpClientNames.Ocr, "error")]
    public Task ActualNamedSocketsSse_FirstAndSecondEventsBeforeEof_OldPoolHeldUntilConsumerAndResponseEnd(string family, string end) => Run(async f =>
    {
        f.Seed("A");
        f.Wire.Respond = (_, _) => "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n" + NetworkWire.ScriptedStream.Chunk("data: first\n\n");
        using var client = f.Client(family); using var request = Request();
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken); f.Responses.Add(response);
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var events = SseParser.ReadEventsAsync(stream, cancellation.Token).GetAsyncEnumerator();
        f.Cleanups.Add(events.DisposeAsync);
        (await events.MoveNextAsync()).ShouldBeTrue(); events.Current.Data.ShouldBe("first");
        var wire = f.Wire.Streams.Last(); wire.EmitChunk("data: second\n\n");
        (await events.MoveNextAsync()).ShouldBeTrue(); events.Current.Data.ShouldBe("second");
        f.Seed("B"); await f.Source.CaptureCurrentAsync(Destination, TestContext.Current.CancellationToken);
        f.Manager.HandlerCount.ShouldBe(1); wire.Disposed.ShouldBeFalse();
        var move = events.MoveNextAsync().AsTask(); f.Work.Add((move, end == "eof" ? null : typeof(Exception)));
        await wire.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var stop = f.Manager.StopAsync(); stop.IsCompleted.ShouldBeFalse(); f.Work.Add((stop, null));
        try
        {
            if (end == "eof") { wire.EndChunks(); (await move).ShouldBeFalse(); }
            else if (end == "cancel") { cancellation.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => move); }
            else { wire.FailRead(); await Should.ThrowAsync<IOException>(() => move); }
        }
        finally { cancellation.Cancel(); wire.EndChunks(); try { await move; } catch (Exception) when (end != "eof") { } }
        f.Manager.HandlerCount.ShouldBe(1); stop.IsCompleted.ShouldBeFalse();
        await events.DisposeAsync(); response.Dispose(); await stop;
        f.Manager.HandlerCount.ShouldBe(0); wire.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task StopAcceptance_WaitsForCaptureAlreadyAdmittedAndAllowsExistingContextRetries() => Run(async f =>
    {
        f.Seed("A"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(); f.Gates.Add(release); f.Release.Add(release.Set);
        f.Secrets.OnGet = () => { entered.TrySetResult(); release.Wait(); };
        using var client = f.Client(HttpClientNames.Llm); using var request = Request();
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken); f.Work.Add((send, null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var stop = f.Manager.StopAsync(); stop.IsCompleted.ShouldBeFalse();
        using var rejected = Request(); await Should.ThrowAsync<IrisException>(() => client.SendAsync(rejected, TestContext.Current.CancellationToken));
        release.Set(); f.Secrets.OnGet = null; f.Responses.Add(await send); stop.IsCompleted.ShouldBeFalse();
        f.Responses.Last().Dispose(); await stop;
    });

    private static HttpRequestMessage Request() => new(HttpMethod.Get, Destination) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };

    private static async Task Run(Func<Scope, Task> body)
    {
        var f = new Scope(); Exception? primary = null; var failures = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Release) release(); f.Wire.ReleaseAll(); f.Secrets.OnGet = null;
            // Join sends/reads first; Stop tasks are joined after response ownership is released.
            foreach (var (work, expected) in f.Work.Where(w => w.Work is Task<HttpResponseMessage> or Task<bool>))
            {
                try { if (work is Task<HttpResponseMessage> send) { var response = await send; if (!f.Responses.Contains(response)) f.Responses.Add(response); } else await work; } catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) failures.Add(ex); }
            }
            foreach (var cleanup in f.Cleanups) try { await cleanup(); } catch (Exception ex) { failures.Add(ex); }
            foreach (var response in f.Responses) try { response.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            try { await f.Manager.StopAsync(); } catch (Exception ex) { failures.Add(ex); }
            foreach (var (work, expected) in f.Work.Where(w => w.Work is not Task<HttpResponseMessage> and not Task<bool>))
                try { await work; } catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) failures.Add(ex); }
            try { f.Services.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            foreach (var gate in f.Gates) gate.Dispose();
        }
        if (primary != null && failures.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) failures.Insert(0, primary);
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private sealed class Scope
    {
        internal readonly Store Settings = new();
        internal readonly SecretsStore Secrets = new();
        internal readonly NetworkWire Wire = new();
        internal readonly WireFactory Factory;
        internal readonly ServiceProvider Services;
        internal readonly NetworkTransportManager Manager;
        internal INetworkProxySnapshotSource Source => Services.GetRequiredService<INetworkProxySnapshotSource>();
        internal readonly List<HttpResponseMessage> Responses = [];
        internal readonly List<(Task Work, Type? Expected)> Work = [];
        internal readonly List<Action> Release = [];
        internal readonly List<Func<ValueTask>> Cleanups = [];
        internal readonly List<IDisposable> Gates = [];
        internal IWebProxy? ResolverProxy;
        internal Scope()
        {
            Factory = new(Wire);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ISettingsService>(Settings); services.AddSingleton<ISecretStore>(Secrets);
            services.AddIrisCore(); services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
            services.AddSingleton<NetworkTransportFactory>(Factory);
            services.AddSingleton(new NetworkRouteResolver { SystemProxy = () => ResolverProxy ?? throw new InvalidOperationException("No synthetic system proxy configured") });
            services.AddIrisProviders(); services.ConfigureAll<HttpStandardResilienceOptions>(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.UseJitter = false; });
            Services = services.BuildServiceProvider(); Manager = Services.GetRequiredService<NetworkTransportManager>();
        }
        internal HttpClient Client(string name) => Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        internal void Seed(string label)
        {
            var id = "pc-" + Guid.NewGuid().ToString("N");
            Secrets.Set(SecretKeys.ProxyCredentialSet(id), JsonSerializer.Serialize(new { format = "iris-proxy-credentials", version = 1,
                proxyUri = ProxyUrl, userName = "synthetic-" + label, password = "password-" + label }));
            Settings.Value = Settings.Value with { Network = new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = ProxyUrl, ProxyCredentialSetId = id } };
        }
    }
    private sealed class WireFactory(NetworkWire wire) : NetworkTransportFactory
    {
        internal List<NetworkRoute> Routes { get; } = [];
        internal List<SocketsHttpHandler> Sockets { get; } = [];
        internal override HttpMessageHandler Create(NetworkRoute route)
        {
            var sockets = (SocketsHttpHandler)base.Create(route); sockets.ConnectCallback = wire.ConnectAsync;
            Routes.Add(route); Sockets.Add(sockets); return sockets;
        }
    }
    private sealed class Store : ISettingsService
    {
        internal AppSettings Value = new();
        public bool IsReadOnly => false;
        public AppSettings Current => Value;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        { var old = Value; Value = mutate(Value); Changed?.Invoke(this, new(old, Value)); return Task.CompletedTask; }
    }
    private sealed class SecretsStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        internal Action? OnGet;
        public event EventHandler<string>? Changed;
        public string? Get(string key) { OnGet?.Invoke(); return _values.GetValueOrDefault(key); }
        public void Set(string key, string value) { _values[key] = value; Changed?.Invoke(this, key); }
        public bool Remove(string key) { bool removed = _values.TryRemove(key, out _); if (removed) Changed?.Invoke(this, key); return removed; }
    }
}
