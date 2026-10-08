using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
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
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;

namespace Iris.Providers.Tests.Http;

public sealed class ProxyConnectivityTests
{
    [Fact]
    public Task Create_IsPureAndSingleUse_AndDraftDoesNotChangeGlobalGeneration() => Run(async f =>
    {
        var current = await f.Source.CaptureCurrentAsync(new("http://origin.example.test/"));
        int reads = f.Settings.Reads;
        var session = f.Create();
        f.Settings.Reads.ShouldBe(reads); f.Secrets.Reads.ShouldBe(0); f.Factory.Created.Count.ShouldBe(0); f.Tester.SessionCount.ShouldBe(1);
        var result = await session.RunAsync();
        result.Status.ShouldBe(ProxyConnectivityStatus.EndpointResponded); result.HttpStatusCode.ShouldBe(200);
        result.RouteKind.ShouldBe(ProxyRouteKind.DirectByMode); result.SessionId.ShouldBe(session.Id);
        f.Manager.ExpectedGeneration.ShouldBe(current.GenerationId); f.Manager.HandlerCount.ShouldBe(0);
        f.Tester.SessionCount.ShouldBe(1); f.Factory.Created.Count.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => session.RunAsync());
        await session.DisposeAsync(); f.Tester.SessionCount.ShouldBe(0);
        f.Factory.Created[0].ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(10)); f.Factory.Created[0].MaxResponseHeadersLength.ShouldBe(16);
        f.Factory.Created[0].UseCookies.ShouldBeFalse(); f.Factory.Created[0].AllowAutoRedirect.ShouldBeFalse();
        f.Requests.ShouldHaveSingleItem(); f.Requests.Single().ShouldStartWith("HEAD ");
        f.Requests.Single().ShouldNotContain("private-provider"); f.Requests.Single().ShouldNotContain("Authorization:");
        f.Requests.Single().ShouldNotContain("Cookie:"); f.Secrets.Reads.ShouldBe(0);
    });

    [Theory]
    [InlineData(200, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(401, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(403, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(404, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(405, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(429, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(503, ProxyConnectivityStatus.EndpointResponded)]
    [InlineData(302, ProxyConnectivityStatus.RedirectNotFollowed)]
    [InlineData(407, ProxyConnectivityStatus.ProxyAuthenticationRequired)]
    public Task RealSockets_ReportsHttpStatusWithoutBusinessRetryOrRedirect(int status, ProxyConnectivityStatus expected) => Run(async f =>
    {
        f.Wire.Respond = (request, _) => { f.Requests.Enqueue(request); return $"HTTP/1.1 {status} Synthetic\r\nLocation: http://private.invalid/secret\r\nContent-Length: 987654\r\n\r\n"; };
        var session = f.Create(); var result = await session.RunAsync();
        result.Status.ShouldBe(expected); result.HttpStatusCode.ShouldBe(status); result.RouteKind.ShouldBe(ProxyRouteKind.DirectByMode);
        await session.DisposeAsync(); f.Requests.Count.ShouldBe(1); f.Factory.Created.Count.ShouldBe(1);
        result.ToString().ShouldNotContain("secret");
    });

    [Fact]
    public Task RealSockets_ProxyAuthenticationUsesDraftCredentialsOnly() => Run(async f =>
    {
        f.Wire.RequireAuth = true;
        var baseline = f.Settings.Value.Network;
        var target = baseline with { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "http://proxy.example.test:8080/" };
        var session = f.Create(new(baseline, target, new(CredentialEditKind.Replace, null, null, new("proxy-user", "proxy-password"))));
        var result = await session.RunAsync();
        result.RouteKind.ShouldBe(ProxyRouteKind.Proxy); result.Status.ShouldBe(ProxyConnectivityStatus.EndpointResponded);
        f.Wire.Auth.ToArray().ShouldBe(["proxy-user:proxy-password"]); f.Wire.Endpoints.Single().ShouldBe("proxy.example.test:8080");
        f.Requests.Single().ShouldNotContain("private-provider"); f.Secrets.Reads.ShouldBe(0);
        f.Settings.Value.Network.ShouldBe(baseline);
    });

    [Theory][InlineData(ProxyRouteKind.DirectByLocalBypass)][InlineData(ProxyRouteKind.DirectBySystemBypass)]
    public Task EffectiveDirectRoute_IsReportedWithoutAssumingProxySuccess(ProxyRouteKind expected) => Run(async f =>
    {
        var baseline = f.Settings.Value.Network;
        if (expected == ProxyRouteKind.DirectByLocalBypass)
            f.Settings.SetAddress("http://localhost/health");
        else
        {
            var proxy = Substitute.For<IWebProxy>(); proxy.IsBypassed(Arg.Any<Uri>()).Returns(true); f.SystemProxy = proxy;
        }
        var result = await f.Create(new(baseline, baseline with { ProxyMode = ProxyMode.System })).RunAsync();
        result.RouteKind.ShouldBe(expected); f.Factory.Created.Single().UseProxy.ShouldBeFalse();
    });

    [Theory][InlineData("http://u:p@origin.example.test")][InlineData("http://origin.example.test?key=private")]
    [InlineData("http://origin.example.test#private")][InlineData("not a url")]
    public Task UnsafeSavedEndpoint_IsRejectedBeforeTransport(string address) => Run(async f =>
    {
        f.Settings.SetAddress(address);
        var result = await f.Create().RunAsync(); result.Status.ShouldBe(ProxyConnectivityStatus.InvalidConfiguration);
        f.Factory.Created.ShouldBeEmpty(); f.Secrets.Reads.ShouldBe(0); result.Error!.Message.ShouldNotContain("private");
    });

    [Fact]
    public Task Endpoints_AreReadAtRunByDomainAndIdWithoutProviderRequirements() => Run(async f =>
    {
        var translation = f.Create(); f.Settings.SetAddress("http://changed.example.test/disabled-no-model");
        (await translation.RunAsync()).Status.ShouldBe(ProxyConnectivityStatus.EndpointResponded);
        f.Requests.Last().ShouldContain("/disabled-no-model"); f.Wire.Endpoints.Last().ShouldBe("changed.example.test:80");
        await translation.DisposeAsync();
        var ocr = f.Create(endpoint: new(NetworkEndpointKind.Ocr, "same")); await ocr.RunAsync();
        f.Wire.Endpoints.Last().ShouldBe("ocr.example.test:80"); await ocr.DisposeAsync();
        var missing = f.Create(endpoint: new(NetworkEndpointKind.Translation, "deleted"));
        (await missing.RunAsync()).Status.ShouldBe(ProxyConnectivityStatus.InvalidConfiguration);
        var windows = f.Create(endpoint: new(NetworkEndpointKind.Ocr, "local"));
        (await windows.RunAsync()).Status.ShouldBe(ProxyConnectivityStatus.InvalidConfiguration);
        f.Factory.Created.Count.ShouldBe(2); f.Secrets.Reads.ShouldBe(0);
    });

    [Fact]
    public Task FutureReadonly_AndCallerCancellationNeverCreateTransport() => Run(async f =>
    {
        f.Settings.ReadOnly = true;
        (await f.Create().RunAsync()).Status.ShouldBe(ProxyConnectivityStatus.InvalidConfiguration);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => f.Create().RunAsync(canceled.Token));
        f.Factory.Created.ShouldBeEmpty(); f.Secrets.Reads.ShouldBe(0);
    });

    [Fact]
    public Task AnthropicDefaultUsesExistingOfficialEndpointWithoutConstructingProvider() => Run(async f =>
    {
        f.Settings.Value = f.Settings.Value with { Translation = f.Settings.Value.Translation with
        { Providers = [f.Settings.Value.Translation.Providers[0] with { Kind = TranslationProviderKind.Anthropic, BaseUrl = null }] } };
        f.Factory.Override = () => new LeafHandler((request, _) =>
        {
            request.RequestUri!.AbsoluteUri.ShouldBe(new Uri(global::Anthropic.Core.EnvironmentUrl.Production).AbsoluteUri);
            request.Method.ShouldBe(HttpMethod.Head); request.Headers.Authorization.ShouldBeNull(); request.Content.ShouldBeNull();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        });
        var result = await f.Create().RunAsync(); result.Status.ShouldBe(ProxyConnectivityStatus.EndpointResponded); result.HttpStatusCode.ShouldBe(403);
        f.Secrets.Reads.ShouldBe(0);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task TimeoutOrCallerCancel_JoinsNoncooperativeSendAndCallerTakesPriority(bool callerCancels) => Run(async f =>
    {
        var gate = f.Gate();
        f.Factory.Override = () => new LeafHandler(async (_, _) => { await gate.PauseAsync(); return new(HttpStatusCode.OK); });
        using var caller = new CancellationTokenSource();
        var session = f.Create(); var run = f.Track(session.RunAsync(caller.Token), callerCancels);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Time.Advance(TimeSpan.FromSeconds(10)); if (callerCancels) caller.Cancel();
        run.IsCompleted.ShouldBeFalse(); f.Tester.SessionCount.ShouldBe(1);
        gate.Open();
        if (callerCancels) await Should.ThrowAsync<OperationCanceledException>(() => run);
        else (await run).Status.ShouldBe(ProxyConnectivityStatus.TimedOut);
        await session.DisposeAsync(); f.Factory.Count.ShouldBe(1);
    });

    [Fact]
    public Task DeadlineIncludesRealCoreKeepCapture_AndDoesNotAbandonSecretRead() => Run(async f =>
    {
        var gate = f.Gate(); string id = "pc-00000000000000000000000000000001";
        f.Settings.Value = f.Settings.Value with { Network = new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "http://proxy.example.test/", ProxyCredentialSetId = id } };
        f.Secrets.Values[SecretKeys.ProxyCredentialSet(id)] = JsonSerializer.Serialize(new { format = "iris-proxy-credentials", version = 1, proxyUri = "http://proxy.example.test/", userName = "proxy", password = "password" });
        f.Secrets.BeforeGet = gate.Pause;
        var run = f.Track(f.Create().RunAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Time.Advance(TimeSpan.FromSeconds(10)); run.IsCompleted.ShouldBeFalse();
        gate.Open(); (await run).Status.ShouldBe(ProxyConnectivityStatus.TimedOut); f.Factory.Count.ShouldBe(0);
    });

    [Fact]
    public Task ResponseHeaderBudget_IsEnforcedByActualSockets() => Run(async f =>
    {
        f.Wire.Respond = (_, _) => "HTTP/1.1 200 OK\r\nX-Long: " + new string('a', 18 * 1024) + "\r\n\r\n";
        (await f.Create().RunAsync()).Status.ShouldBe(ProxyConnectivityStatus.TransportFailed);
        f.Factory.Created.Single().MaxResponseHeadersLength.ShouldBe(16);
    });

    [Theory][InlineData("content")][InlineData("response")][InlineData("handler")]
    public Task CleanupFailure_RetainsActualObjectAndRetryNeverSendsAgain(string phase) => Run(async f =>
    {
        var content = new LeafContent { Fail = phase == "content" }; var response = new LeafResponse { Content = content, Fail = phase == "response" };
        var handler = new LeafHandler((_, _) => Task.FromResult<HttpResponseMessage>(response)) { FailDispose = phase == "handler" };
        f.Release.Add(() => { content.Fail = response.Fail = handler.FailDispose = false; });
        f.Factory.Override = () => handler;
        var session = f.Create(); (await session.RunAsync()).Status.ShouldBe(ProxyConnectivityStatus.EndpointResponded);
        content.Reads.ShouldBe(0);
        await Should.ThrowAsync<IrisException>(() => session.DisposeAsync().AsTask()); f.Tester.SessionCount.ShouldBe(1);
        await Should.ThrowAsync<IrisException>(() => f.Lifetime.StopAsync());
        content.Fail = response.Fail = handler.FailDispose = false;
        await session.DisposeAsync(); await f.Lifetime.StopAsync();
        handler.Sends.ShouldBe(1); f.Factory.Count.ShouldBe(1); f.Tester.SessionCount.ShouldBe(0);
        content.Disposed.ShouldBeTrue(); handler.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task CompositeStop_OwnsNeverRunAndRunningSessions_AndSharesCleanupInFlight() => Run(async f =>
    {
        var unused = f.Create(); var gate = f.Gate(); var content = new LeafContent { BeforeDispose = gate.Pause };
        f.Factory.Override = () => new LeafHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var used = f.Create(); await used.RunAsync();
        var closing = used.DisposeAsync().AsTask(); _ = f.Track(closing);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        used.DisposeAsync().AsTask().ShouldBeSameAs(closing);
        var stop = f.Lifetime.StopAsync(); _ = f.Track(stop); stop.IsCompleted.ShouldBeFalse(); f.Lifetime.StopAsync().ShouldBeSameAs(stop);
        Should.Throw<ObjectDisposedException>(() => f.Create());
        gate.Open(); await closing; await stop; f.Tester.SessionCount.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(() => unused.RunAsync());
    });

    [Theory][InlineData("caller")][InlineData("dispose")][InlineData("stop")]
    public Task CancellationCallbackFailure_IsReportedOnce_ThenOriginalSessionCleanupCanRetry(string trigger) => Run(async f =>
    {
        var sendGate = f.Gate(); var callbackGate = f.Gate();
        CancellationTokenRegistration registration = default;
        f.OnDraftCapture = token => registration = token.Register(() =>
        {
            callbackGate.Pause();
            throw new IOException("synthetic-cancel-callback");
        });
        f.Release.Add(() => registration.Dispose());
        var handler = new LeafHandler(async (_, _) => { await sendGate.PauseAsync(); return new(HttpStatusCode.OK); });
        f.Factory.Override = () => handler;
        using var caller = new CancellationTokenSource();
        var session = f.Create(); var run = f.Track(session.RunAsync(caller.Token), canceled: true);
        await sendGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (trigger == "caller") caller.Cancel();
        var first = trigger == "stop" ? f.Lifetime.StopAsync() : session.DisposeAsync().AsTask();
        var firstFailure = f.Track(Should.ThrowAsync<IrisException>(() => first));
        await callbackGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.IsCompleted.ShouldBeFalse(); run.IsCompleted.ShouldBeFalse(); handler.Disposed.ShouldBeFalse();
        var joinedDispose = session.DisposeAsync().AsTask();
        session.DisposeAsync().AsTask().ShouldBeSameAs(joinedDispose);
        if (trigger == "stop") f.Lifetime.StopAsync().ShouldBeSameAs(first);
        callbackGate.Open(); await registration.DisposeAsync();
        // Even an ended, faulted callback cannot stand in for the noncooperative send exiting.
        first.IsCompleted.ShouldBeFalse(); handler.Disposed.ShouldBeFalse();
        sendGate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => run);
        (await firstFailure).Code.ShouldBe(ErrorCode.NetworkUnavailable);
        await Should.ThrowAsync<IrisException>(() => joinedDispose);
        f.Tester.SessionCount.ShouldBe(1); handler.Disposed.ShouldBeFalse(); handler.Sends.ShouldBe(1);
        await session.DisposeAsync(); await f.Lifetime.StopAsync();
        f.Tester.SessionCount.ShouldBe(0); handler.Disposed.ShouldBeTrue(); handler.Sends.ShouldBe(1); f.Factory.Count.ShouldBe(1);
    }, captureCallback: true);

    private static async Task Run(Func<Fixture, Task> body, bool captureCallback = false)
    {
        var f = new Fixture(captureCallback); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var gate in f.Gates) gate.Open();
            foreach (var release in f.Release) release(); f.Wire.ReleaseAll(); f.Secrets.BeforeGet = null;
            foreach (var (task, canceled) in f.Work)
                try { await task; } catch (OperationCanceledException) when (canceled) { } catch (Exception ex) { errors.Add(ex); }
            foreach (var session in f.Sessions) try { await session.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Lifetime.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { f.Services.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var gate in f.Gates) gate.Dispose();
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Fixture
    {
        internal readonly Store Settings = new(); internal readonly Secrets Secrets = new();
        internal readonly FakeTimeProvider Time = new(); internal readonly NetworkWire Wire = new() { RequireAuth = false };
        internal readonly ConcurrentQueue<string> Requests = new();
        internal readonly ServiceProvider Services; internal readonly TransportFactory Factory;
        internal readonly ProxyConnectivityTester Tester; internal readonly INetworkTransportLifetime Lifetime;
        internal readonly NetworkTransportManager Manager; internal INetworkProxySnapshotSource Source => Services.GetRequiredService<INetworkProxySnapshotSource>();
        internal readonly List<IProxyConnectivityTestSession> Sessions = []; internal readonly List<(Task Task, bool Canceled)> Work = [];
        internal readonly List<Gate> Gates = []; internal readonly List<Action> Release = [];
        internal IWebProxy? SystemProxy;
        internal Action<CancellationToken>? OnDraftCapture;
        internal Fixture(bool captureCallback = false)
        {
            Factory = new(Wire); Wire.Respond = (request, _) => { Requests.Enqueue(request); return "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"; };
            var services = new ServiceCollection(); services.AddLogging(); services.AddIrisCore(); services.AddIrisProviders();
            services.AddSingleton<ISettingsService>(Settings); services.AddSingleton<ISecretStore>(Secrets); services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
            services.AddSingleton<ProxyConnectivityTransportFactory>(Factory);
            services.AddSingleton(new NetworkRouteResolver { SystemProxy = () => SystemProxy ?? throw new IOException("Synthetic system proxy unavailable") });
            if (captureCallback) services.AddSingleton<INetworkProxySnapshotSource>(sp =>
                new CallbackSource((INetworkProxySnapshotSource)sp.GetRequiredService<ISettingsEditCoordinator>(), token => OnDraftCapture?.Invoke(token)));
            Services = services.BuildServiceProvider(); Tester = Services.GetRequiredService<ProxyConnectivityTester>();
            Lifetime = Services.GetRequiredService<INetworkTransportLifetime>(); Manager = Services.GetRequiredService<NetworkTransportManager>();
        }
        internal IProxyConnectivityTestSession Create(ProxyDraft? draft = null, NetworkEndpointSelection? endpoint = null)
        {
            var baseline = Settings.Value.Network;
            var session = Tester.Create(draft ?? new(baseline, baseline), endpoint ?? new(NetworkEndpointKind.Translation, "same")); Sessions.Add(session); return session;
        }
        internal Gate Gate() { var gate = new Gate(); Gates.Add(gate); return gate; }
        internal T Track<T>(T task, bool canceled = false) where T : Task { Work.Add((task, canceled)); return task; }
    }
    private sealed class CallbackSource(INetworkProxySnapshotSource inner, Action<CancellationToken> onCapture) : INetworkProxySnapshotSource
    {
        public Task<NetworkProxySnapshot> CaptureCurrentAsync(Uri destination, CancellationToken cancellationToken = default)
            => inner.CaptureCurrentAsync(destination, cancellationToken);
        public Task<NetworkProxySnapshot> CaptureDraftAsync(ProxyDraft draft, Uri destination, CancellationToken cancellationToken = default)
        {
            onCapture(cancellationToken);
            return inner.CaptureDraftAsync(draft, destination, cancellationToken);
        }
    }
    private sealed class Store : ISettingsService
    {
        internal AppSettings Value = new()
        {
            Network = new() { ProxyMode = ProxyMode.None },
            Translation = new() { Providers = [new() { Id = "same", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "disabled", Enabled = false, Model = "", BaseUrl = "http://origin.example.test/v1", CredentialSetId = "private-provider", OpenAIOptions = new() { ExtraHeaders = new Dictionary<string, string> { ["Authorization"] = "private-provider" } } }] },
            Ocr = new() { Providers = [new() { Id = "same", Kind = OcrProviderKind.BaiduCloud, DisplayName = "OCR", Enabled = false, BaiduOptions = new() { BaseUrl = "http://ocr.example.test/" } }, new() { Id = "local", Kind = OcrProviderKind.WindowsLocal, DisplayName = "Local" }] }
        };
        internal int Reads; internal bool ReadOnly;
        public bool IsReadOnly => ReadOnly;
        public AppSettings Current { get { Reads++; return Value; } }
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        internal void SetAddress(string address) => Value = Value with { Translation = Value.Translation with { Providers = [Value.Translation.Providers[0] with { BaseUrl = address }] } };
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        { var old = Value; Value = mutate(Value); Changed?.Invoke(this, new(old, Value)); return Task.CompletedTask; }
    }
    private sealed class Secrets : ISecretStore
    {
        internal readonly ConcurrentDictionary<string, string> Values = new(); internal int Reads; internal Action? BeforeGet;
        public event EventHandler<string>? Changed;
        public string? Get(string key) { Interlocked.Increment(ref Reads); BeforeGet?.Invoke(); return Values.GetValueOrDefault(key); }
        public void Set(string key, string value) { Values[key] = value; Changed?.Invoke(this, key); }
        public bool Remove(string key) => Values.TryRemove(key, out _);
    }
    private sealed class TransportFactory(NetworkWire wire) : ProxyConnectivityTransportFactory
    {
        internal int Count; internal readonly List<SocketsHttpHandler> Created = []; internal Func<HttpMessageHandler>? Override;
        internal override HttpMessageHandler Create(NetworkRoute route)
        {
            Count++; if (Override != null) return Override();
            var sockets = (SocketsHttpHandler)base.Create(route); sockets.ConnectCallback = wire.ConnectAsync; Created.Add(sockets); return sockets;
        }
    }
    private sealed class Gate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(); private readonly TaskCompletionSource _async = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task PauseAsync() { Entered.TrySetResult(); return _async.Task; }
        internal void Pause() { Entered.TrySetResult(); _release.Wait(); }
        internal void Open() { _release.Set(); _async.TrySetResult(); }
        public void Dispose() => _release.Dispose();
    }
    private sealed class LeafHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends; internal bool FailDispose, Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Sends++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) { if (FailDispose) throw new IOException("synthetic-handler"); Disposed = true; } base.Dispose(disposing); }
    }
    private sealed class LeafContent : HttpContent
    {
        internal bool Fail, Disposed; internal int Reads; internal Action? BeforeDispose;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Reads++; throw new IOException("HEAD must not read body"); }
        protected override bool TryComputeLength(out long length) { length = 987654; return true; }
        protected override void Dispose(bool disposing) { if (disposing) { BeforeDispose?.Invoke(); if (Fail) throw new IOException("synthetic-content"); Disposed = true; } base.Dispose(disposing); }
    }
    private sealed class LeafResponse : HttpResponseMessage
    {
        internal bool Fail;
        protected override void Dispose(bool disposing) { if (disposing && Fail) throw new IOException("synthetic-response"); base.Dispose(disposing); }
    }
}
