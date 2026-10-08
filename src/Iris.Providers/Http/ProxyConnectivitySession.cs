using System.Net;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Settings;

namespace Iris.Providers.Http;

internal sealed class ProxyConnectivitySession : IProxyConnectivityTestSession
{
    private readonly Lock _gate = new();
    private readonly ProxyConnectivityTester _owner;
    private readonly INetworkProxySnapshotSource _source;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly NetworkRouteResolver _resolver;
    private readonly ProxyConnectivityTransportFactory _factory;
    private readonly CancellationTokenSource _cancel = new();
    private ProxyDraft? _draft;
    private NetworkEndpointSelection? _endpoint;
    private Task<ProxyConnectivityResult>? _run;
    private Task? _dispose, _cancelWork;
    private bool _closing, _disposed, _timedOut, _runEnded, _cancelCallbacksJoined;
    private CancellationTokenRegistration _callerRegistration;
    private ITimer? _timer;
    private HttpMessageHandler? _handler;
    private HttpClient? _client;
    private HttpResponseMessage? _response;
    private HttpContent? _content;

    internal ProxyConnectivitySession(ProxyConnectivityTester owner, ProxyDraft draft, NetworkEndpointSelection endpoint,
        INetworkProxySnapshotSource source, ISettingsService settings, TimeProvider time, NetworkRouteResolver resolver, ProxyConnectivityTransportFactory factory)
    { _owner = owner; _draft = draft; _endpoint = endpoint; _source = source; _settings = settings; _time = time; _resolver = resolver; _factory = factory; }
    public Guid Id { get; } = Guid.NewGuid();

    public Task<ProxyConnectivityResult> RunAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<ProxyConnectivityResult> completion;
        long started;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing || _disposed, this);
            if (_run != null) throw new InvalidOperationException("A connectivity session can run only once.");
            started = _time.GetTimestamp();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _run = completion.Task;
        }
        _ = RunAndPublishAsync(completion, started, cancellationToken);
        return completion.Task;
    }

    private async Task RunAndPublishAsync(TaskCompletionSource<ProxyConnectivityResult> completion, long started, CancellationToken caller)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Guid generation = Guid.Empty;
        NetworkRoute? route = null;
        ProxyConnectivityResult Result(ProxyConnectivityStatus status, int? http = null, ErrorCode? code = null) =>
            new(Id, generation, status, route?.Kind ?? ProxyRouteKind.Proxy, _time.GetElapsedTime(started), http,
                code is { } error ? new ErrorInfo(error, ErrorMessages.GetDefault(error)) : null);
        ProxyConnectivityResult? result = null;
        try
        {
            _callerRegistration = caller.UnsafeRegister(static state => ((ProxyConnectivitySession)state!).RequestCancel(), this);
            var remaining = TimeSpan.FromSeconds(10) - _time.GetElapsedTime(started);
            _timer = _time.CreateTimer(static state => ((ProxyConnectivitySession)state!).Timeout(), this,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, System.Threading.Timeout.InfiniteTimeSpan);
            if (remaining <= TimeSpan.Zero) Timeout();
            CheckCancellation(caller);
            if (_settings.IsReadOnly) throw NetworkProxyCodec.StorageFailure();
            var destination = ResolveEndpoint(_settings.Current, _endpoint!);
            var snapshot = await _source.CaptureDraftAsync(_draft!, destination, _cancel.Token).ConfigureAwait(false);
            generation = snapshot.GenerationId;
            CheckCancellation(caller);
            route = _resolver.Resolve(snapshot);
            CheckCancellation(caller);
            _handler = _factory.Create(route);
            _client = new HttpClient(_handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Head, destination);
            _response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancel.Token).ConfigureAwait(false);
            _content = _response.Content;
            CheckCancellation(caller);
            int status = (int)_response.StatusCode;
            result = Result(status == 407 ? ProxyConnectivityStatus.ProxyAuthenticationRequired
                : status is >= 300 and < 400 ? ProxyConnectivityStatus.RedirectNotFollowed : ProxyConnectivityStatus.EndpointResponded, status);
        }
        catch (OperationCanceledException) { /* The caller/close/timeout priority is resolved below, after actual work exits. */ }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired && route != null)
        { result = Result(ProxyConnectivityStatus.ProxyAuthenticationRequired, 407); }
        catch (IrisException ex) when (ex.Code is ErrorCode.InvalidArgument or ErrorCode.SecretUnavailable or ErrorCode.StorageFailure)
        { result = Result(ProxyConnectivityStatus.InvalidConfiguration, code: ex.Code); }
        catch (Exception) { result = Result(ProxyConnectivityStatus.TransportFailed, code: ErrorCode.NetworkUnavailable); }
        finally
        {
            lock (_gate) { _runEnded = true; _draft = null; _endpoint = null; }
        }
        lock (_gate)
        {
            if (caller.IsCancellationRequested || _closing) completion.TrySetCanceled(caller.IsCancellationRequested ? caller : _cancel.Token);
            else if (_timedOut) completion.TrySetResult(Result(ProxyConnectivityStatus.TimedOut, code: ErrorCode.Timeout));
            else completion.TrySetResult(result ?? Result(ProxyConnectivityStatus.TransportFailed, code: ErrorCode.NetworkUnavailable));
        }
    }

    // Only fields required to select the saved endpoint are read; no provider factory or secret key is used.
    private static Uri ResolveEndpoint(AppSettings settings, NetworkEndpointSelection endpoint)
    {
        string? address;
        if (endpoint.Kind == NetworkEndpointKind.Translation)
        {
            var profile = settings.Translation.Providers.SingleOrDefault(p => p.Id == endpoint.ProviderId) ?? throw NetworkProxyCodec.Invalid();
            address = profile.Kind switch
            {
                TranslationProviderKind.OpenAICompatible => profile.BaseUrl,
                TranslationProviderKind.Anthropic => string.IsNullOrWhiteSpace(profile.BaseUrl) ? global::Anthropic.Core.EnvironmentUrl.Production : profile.BaseUrl,
                _ => throw NetworkProxyCodec.Invalid()
            };
        }
        else if (endpoint.Kind == NetworkEndpointKind.Ocr)
        {
            var profile = settings.Ocr.Providers.SingleOrDefault(p => p.Id == endpoint.ProviderId && p.Kind == OcrProviderKind.BaiduCloud) ?? throw NetworkProxyCodec.Invalid();
            address = (profile.BaiduOptions ?? new()).BaseUrl;
        }
        else throw NetworkProxyCodec.Invalid();
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl) || address != address.Trim()
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https"
            || uri.UserInfo.Length != 0 || NetworkProxyCodec.HasUserInfo(address) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw NetworkProxyCodec.Invalid();
        return uri;
    }

    private void CheckCancellation(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate) if (_closing || _timedOut) throw new OperationCanceledException(_cancel.Token);
        _cancel.Token.ThrowIfCancellationRequested();
    }
    private void Timeout()
    {
        lock (_gate) { if (_runEnded || _disposed) return; _timedOut = true; }
        RequestCancel();
    }
    private void RequestCancel()
    {
        lock (_gate)
        {
            if (_disposed || _cancelWork != null) return;
            // Caller cancellation callbacks stay short; Dispose joins the actual asynchronous callbacks.
            _cancelWork = _cancel.CancelAsync();
        }
    }
    internal void StopAcceptance()
    {
        lock (_gate) { if (_disposed) return; _closing = true; }
        RequestCancel();
    }

    public ValueTask DisposeAsync()
    {
        StopAcceptance();
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (_dispose is { IsCompleted: false }) return new(_dispose);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _dispose = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            Task? run; lock (_gate) run = _run;
            if (run != null) try { await run.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await _callerRegistration.DisposeAsync().ConfigureAwait(false);
            if (_timer != null) { await _timer.DisposeAsync().ConfigureAwait(false); _timer = null; }
            Task? cancel; lock (_gate) cancel = _cancelWork;
            if (cancel != null && !_cancelCallbacksJoined)
            {
                // A fault still means every cancellation callback has exited. Report it on
                // this attempt, then let the next Dispose continue with the retained resources.
                try { await cancel.ConfigureAwait(false); }
                finally { _cancelCallbacksJoined = true; }
            }
            // Retain each actual failing object; never retry a previously disposed response/client wrapper.
            if (_content != null)
            {
                _content.Dispose();
                _response!.Content = null;
                _content = null;
            }
            if (_response != null) { _response.Dispose(); _response = null; }
            if (_client != null) { _client.Dispose(); _client = null; }
            if (_handler != null) { _handler.Dispose(); _handler = null; }
            _cancel.Dispose();
            _owner.Released(this, completion);
        }
        catch (Exception) { completion.TrySetException(NetworkProxyCodec.Unavailable()); }
    }
    internal void CompleteDispose()
    { lock (_gate) { _disposed = true; _draft = null; _endpoint = null; } }
}
