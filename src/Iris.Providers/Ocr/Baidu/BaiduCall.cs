using Iris.Core.Errors;

namespace Iris.Providers.Ocr.Baidu;

internal sealed class BaiduCall : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationToken _caller;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly string _id;
    private CancellationTokenRegistration _registration;
    private ITimer? _timer;
    private Task? _cancellation;
    private bool _sealed, _registrationDisposed, _sourceDisposed;
    internal bool HasPendingCleanup => _timer != null || !_registrationDisposed || !_sourceDisposed;
    private volatile bool _stopped;
    internal long Started { get; }
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Token => _source.Token;
    internal bool UserCanceled => _caller.IsCancellationRequested || _stopped;
    internal BaiduCall(TimeProvider time, TimeSpan timeout, string id, CancellationToken caller)
    { _caller = caller; _time = time; _timeout = timeout; _id = id; Started = time.GetTimestamp(); }
    internal void Start()
    {
        _registration = _caller.Register(() => Cancel(false));
        var remaining = _timeout - _time.GetElapsedTime(Started);
        _timer = _time.CreateTimer(_ => Cancel(false), null, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
    internal void Cancel(bool stop)
    {
        lock (_gate)
        {
            _stopped |= stop;
            if (!_sealed) _cancellation ??= Task.Run(async () => await _source.CancelAsync().ConfigureAwait(false));
        }
    }
    internal void Check()
    {
        if (UserCanceled) throw new OperationCanceledException(_caller.IsCancellationRequested ? _caller : new CancellationToken(true));
        if (_time.GetElapsedTime(Started) >= _timeout) throw new BaiduException(ErrorCode.Timeout, "PROFILE_TIMEOUT", _id, retryable: true);
    }
    internal Exception Classify(string stage, Exception error)
    {
        if (error is OperationCanceledException)
        {
            try { Check(); } catch (Exception cancellation) { return cancellation; }
        }
        return BaiduErrors.External(stage, error, _id);
    }
    public async ValueTask DisposeAsync()
    {
        var errors = new List<string>();
        if (_timer != null)
            try { await _timer.DisposeAsync().ConfigureAwait(false); _timer = null; }
            catch (Exception ex) { errors.Add(BaiduErrors.Describe("TIMER", ex)); }
        try { await _registration.DisposeAsync().ConfigureAwait(false); _registrationDisposed = true; }
        catch (Exception ex) { errors.Add(BaiduErrors.Describe("CALLER_REGISTRATION", ex)); }
        Task? cancellation;
        lock (_gate) { _sealed = true; cancellation = _cancellation; _cancellation = null; }
        try { if (cancellation != null) await cancellation.ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(BaiduErrors.Describe("CANCEL", ex)); }
        if (!_sourceDisposed)
            try { _source.Dispose(); _sourceDisposed = true; }
            catch (Exception ex) { errors.Add(BaiduErrors.Describe("CANCELLATION_SOURCE", ex)); }
        if (errors.Count != 0) throw new BaiduException(ErrorCode.Unknown, "CALL_CLEANUP", _id, diagnostics: string.Join(';', errors));
    }
}
