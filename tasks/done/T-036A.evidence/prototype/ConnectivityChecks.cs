using System.Net;
using Microsoft.Extensions.Time.Testing;

internal enum ProbeKind { EndpointResponded, RedirectNotFollowed, ProxyAuthenticationRequired, Timeout, TransportFailed, InvalidConfiguration }
internal sealed record ProbeResult(Guid SessionId, Guid SnapshotId, ProbeKind Kind, int? Status)
{ public override string ToString() => $"ProxyProbe {Kind}"; }
internal sealed class DraftTransport(HttpMessageHandler handler) : IAsyncDisposable
{
    internal readonly HttpMessageInvoker Invoker = new(handler);
    internal bool FailDispose;
    internal Func<Task>? BeforeDispose;
    internal int DisposeAttempts;
    internal bool Disposed;
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        DisposeAttempts++; if (BeforeDispose != null) await BeforeDispose();
        if (FailDispose) throw new IOException("Synthetic transport cleanup failure.");
        Invoker.Dispose(); Disposed = true;
    }
}
internal sealed class DraftProbeSession(Func<CancellationToken, Task<ManagedSnapshot>> capture, Uri endpoint,
    Func<ManagedSnapshot, DraftTransport> factory, TimeProvider time) : IAsyncDisposable
{
    private Func<CancellationToken, Task<ManagedSnapshot>>? _capture = capture;
    private Func<ManagedSnapshot, DraftTransport>? _factory = factory;
    private Uri? _endpoint = endpoint;
    internal Guid Id { get; } = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private Task<ProbeResult>? _run;
    private Task? _dispose;
    private bool _closed;
    private DraftTransport? _transport;
    private HttpResponseMessage? _response;
    private HttpContent? _content;
    private ManagedSnapshot? _snapshot;
    internal bool OwnsSnapshot => _snapshot != null;
    internal Task<ProbeResult> RunAsync(CancellationToken caller = default)
    {
        TaskCompletionSource<ProbeResult> completion;
        lock (_gate)
        {
            if (_closed || _dispose != null) throw new ObjectDisposedException(nameof(DraftProbeSession));
            if (_run != null) throw new InvalidOperationException("One Run per draft session.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _run = completion.Task;
        }
        _ = CompleteRunAsync(completion, caller); return completion.Task;
    }
    private async Task CompleteRunAsync(TaskCompletionSource<ProbeResult> completion, CancellationToken caller)
    {
        try { completion.TrySetResult(await RunCoreAsync(caller)); }
        catch (OperationCanceledException) { completion.TrySetCanceled(caller.IsCancellationRequested ? caller : _closing.Token); }
        catch (Exception) { completion.TrySetResult(new(Id, _snapshot?.Version ?? Guid.Empty, ProbeKind.TransportFailed, null)); }
    }
    private async Task<ProbeResult> RunCoreAsync(CancellationToken caller)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _closing.Token, timeout.Token);
        try
        {
            var endpoint = _endpoint!;
            if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                return new(Id, Guid.Empty, ProbeKind.InvalidConfiguration, null);
            _snapshot = await _capture!(linked.Token); linked.Token.ThrowIfCancellationRequested(); _transport = _factory!(_snapshot);
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            _response = await _transport.Invoker.SendAsync(request, linked.Token); _content = _response.Content;
            linked.Token.ThrowIfCancellationRequested();
            int code = (int)_response.StatusCode;
            var kind = code == 407 ? ProbeKind.ProxyAuthenticationRequired : code is >= 300 and < 400 ? ProbeKind.RedirectNotFollowed : ProbeKind.EndpointResponded;
            return new(Id, _snapshot.Version, kind, code);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested || _closing.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return new(Id, _snapshot?.Version ?? Guid.Empty, ProbeKind.Timeout, null); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) { return new(Id, _snapshot?.Version ?? Guid.Empty, ProbeKind.ProxyAuthenticationRequired, 407); }
        catch (Exception) { return new(Id, _snapshot?.Version ?? Guid.Empty, ProbeKind.TransportFailed, null); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closed) return ValueTask.CompletedTask;
            if (_dispose is { IsCompleted: false }) return new(_dispose);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _dispose = completion.Task;
        }
        _ = CompleteDisposeAsync(completion); return new(completion.Task);
    }
    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await _closing.CancelAsync();
            if (_run != null) try { await _run; } catch (OperationCanceledException) { }
            if (_content != null) { await Task.Run(() => _content.Dispose()); _content = null; }
            if (_response != null) { _response.Dispose(); _response = null; }
            if (_transport != null) { await _transport.DisposeAsync(); _transport = null; }
            _snapshot = null; _capture = null; _factory = null; _endpoint = null; _closing.Dispose(); lock (_gate) _closed = true;
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
internal sealed class ProbeHandler(int status) : HttpMessageHandler
{
    internal int Calls;
    internal Func<Task>? BeforeHeaders;
    internal readonly TrackedBody Body = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++; Check.That(request.Method == HttpMethod.Head && request.Content == null && !request.Headers.Any(), "probe must contain no provider auth/text/default headers");
        if (BeforeHeaders != null) await BeforeHeaders();
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = Body };
        response.Headers.Location = new Uri("https://not-followed.example.test/private"); return response;
    }
}
internal sealed class TrackedBody : HttpContent
{
    internal bool Disposed, FailDispose;
    internal int Reads, DisposeCalls;
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Reads++; throw new InvalidOperationException("HEAD probe must not read payload"); }
    protected override bool TryComputeLength(out long length) { length = 42; return true; }
    protected override void Dispose(bool disposing)
    {
        if (!Disposed && disposing) { DisposeCalls++; if (FailDispose) throw new IOException("Synthetic content cleanup failure"); Disposed = true; }
        base.Dispose(disposing);
    }
}
internal static class ConnectivityChecks
{
    internal static async Task Run()
    {
        var snapshot = new ManagedSnapshot(Guid.NewGuid(), new(Iris.Core.Settings.ProxyMode.Custom, "http://proxy.example.test/", false), new("synthetic", "synthetic"));
        foreach (int status in new[] { 200, 401, 403, 405, 407, 302, 503 })
        {
            var handler = new ProbeHandler(status); var transport = new DraftTransport(handler); int captures = 0;
            var session = new DraftProbeSession(_ => { captures++; return Task.FromResult(snapshot); }, new("https://configured-service.example.test/"), _ => transport, TimeProvider.System);
            Check.That(captures == 0 && handler.Calls == 0, "session creation must be pure");
            try
            {
                var result = await session.RunAsync();
                Check.That(result.Kind == (status == 407 ? ProbeKind.ProxyAuthenticationRequired : status == 302 ? ProbeKind.RedirectNotFollowed : ProbeKind.EndpointResponded), "status classification");
                Check.That(session.OwnsSnapshot && !transport.Disposed && !handler.Body.Disposed && handler.Body.Reads == 0, "Run completion is not resource disposal");
                bool repeated = false; try { await session.RunAsync(); } catch (InvalidOperationException) { repeated = true; }
                Check.That(repeated && handler.Calls == 1, "Run must be single-use");
            }
            finally { await session.DisposeAsync(); }
            Check.That(handler.Body.Disposed && transport.Disposed && !session.OwnsSnapshot, "response and transport released");
            Check.Pass("proposal connectivity HEAD status=" + status + " noProviderKey=true noBodyRead=true noRedirect=true singleRun=true");
        }
        await CancellationAndRetry(snapshot);
        var wire = new ProxyWire(); var draft = snapshot with { Credentials = new("synthetic-draft", "synthetic-secret") };
        var global = snapshot with { Settings = new(Iris.Core.Settings.ProxyMode.None, null, false) };
        var originalGlobal = global;
        var actualSession = new DraftProbeSession(_ => Task.FromResult(draft), new("http://configured-service.example.test/"), captured =>
        {
            var route = ResolveRoute.Capture(captured, new("http://configured-service.example.test/"), new WebProxy());
            return new DraftTransport(wire.Handler(new FrozenProxy(route.Uri!, route.Credentials)));
        }, TimeProvider.System);
        try
        {
            var result = await actualSession.RunAsync();
            Check.That(result.Kind == ProbeKind.EndpointResponded && wire.Methods.All(m => m == "HEAD") && !wire.OriginAuthorizationSeen, "actual draft transport only HEAD, no provider auth");
            Check.That(wire.Endpoints.Single() == "proxy.example.test:80" && wire.AuthorizationLabels.Single() == "draft" && global == originalGlobal, "draft route/auth independent of global None policy");
        }
        finally { await actualSession.DisposeAsync(); }
        Check.Pass("actual runtime draft HEAD traverses selected proxy auth, leaves global policy unchanged, sends no Provider auth; in-memory ConnectCallback only");
        var deniedWire = new ProxyWire();
        var deniedSession = new DraftProbeSession(_ => Task.FromResult(draft), new("https://configured-service.example.test/"), _ =>
            new DraftTransport(deniedWire.Handler(new FrozenProxy(new("http://proxy.example.test/"), null))), TimeProvider.System);
        try
        {
            var denied = await deniedSession.RunAsync();
            Check.That(denied.Kind == ProbeKind.ProxyAuthenticationRequired && denied.Status == 407 && deniedWire.Methods.Single() == "CONNECT", "actual HTTPS CONNECT 407 is typed without parsing raw exception text");
        }
        finally { await deniedSession.DisposeAsync(); }
        Check.Pass("actual runtime HTTPS CONNECT 407 classified via HttpRequestException.StatusCode; no TLS/server/socket used");
    }
    private static async Task CancellationAndRetry(ManagedSnapshot snapshot)
    {
        foreach (bool timeout in new[] { false, true })
        {
            var gate = new Gate(); var handler = new ProbeHandler(200) { BeforeHeaders = gate.Pause }; var transport = new DraftTransport(handler); var time = new FakeTimeProvider();
            var session = new DraftProbeSession(_ => Task.FromResult(snapshot), new("https://configured-service.example.test"), _ => transport, time);
            using var caller = new CancellationTokenSource(); var run = session.RunAsync(caller.Token); Task? disposing = null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (timeout) time.Advance(TimeSpan.FromSeconds(10)); else caller.Cancel();
                Check.That(!run.IsCompleted && !transport.Disposed, "noncooperative send cannot be abandoned on timeout/cancel");
                if (!timeout) { disposing = session.DisposeAsync().AsTask(); Check.That(!disposing.IsCompleted, "close must await actual send"); }
            }
            finally
            {
                gate.Open();
                if (timeout) Check.That((await run).Kind == ProbeKind.Timeout, "timeout outcome after real I/O exit");
                else { try { await run; } catch (OperationCanceledException) { } }
                if (disposing != null) await disposing; else await session.DisposeAsync();
            }
            Check.That(handler.Calls == 1 && transport.Disposed, "no repeated send on cancellation cleanup");
            Check.Pass("proposal connectivity noncooperative " + (timeout ? "timeout" : "cancel/close") + " actual task joined");
        }
        foreach (bool contentFailure in new[] { false, true })
        {
            var handler = new ProbeHandler(200); var transport = new DraftTransport(handler); var session = new DraftProbeSession(_ => Task.FromResult(snapshot), new("https://configured-service.example.test"), _ => transport, TimeProvider.System);
            await session.RunAsync(); if (contentFailure) handler.Body.FailDispose = true; else transport.FailDispose = true;
            bool failed = false;
            try { await session.DisposeAsync(); } catch (IOException) { failed = true; }
            try
            {
                Check.That(failed && session.OwnsSnapshot && handler.Calls == 1, "failed cleanup keeps original snapshot/session");
            }
            finally { handler.Body.FailDispose = false; transport.FailDispose = false; await session.DisposeAsync(); }
            Check.That(handler.Calls == 1 && transport.Disposed && !session.OwnsSnapshot, "retry cleans original resource without reissuing HEAD");
            Check.Pass("proposal connectivity " + (contentFailure ? "response-content" : "transport") + " cleanup failure retained/retried; sends=1");
        }
        var cleanupGate = new Gate(); var blockedTransport = new DraftTransport(new ProbeHandler(200)) { BeforeDispose = cleanupGate.Pause };
        var owned = new DraftProbeSession(_ => Task.FromResult(snapshot), new("https://configured-service.example.test"), _ => blockedTransport, TimeProvider.System);
        await owned.RunAsync(); var cleanup = owned.DisposeAsync().AsTask();
        try { await cleanupGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check.That(!cleanup.IsCompleted && owned.OwnsSnapshot, "save/close barrier must include transport cleanup"); }
        finally { cleanupGate.Open(); await cleanup; }
        Check.Pass("proposal connectivity cleanup gate holds snapshot through full disposal");
    }
}
