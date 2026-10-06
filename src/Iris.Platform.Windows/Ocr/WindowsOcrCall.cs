using Iris.Core.Errors;

namespace Iris.Platform.Windows.Ocr;

internal sealed class WindowsOcrCall : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _budget;
    private readonly CancellationToken _caller;
    private readonly string _id;
    private CancellationTokenRegistration _registration;
    private ITimer? _timer;
    private Task? _cancel;
    private bool _sealed, _registrationDisposed, _sourceDisposed;
    private volatile bool _stopped;
    internal long Started { get; }
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Token => _source.Token;
    internal bool Pending => _timer != null || !_registrationDisposed || !_sourceDisposed;
    internal WindowsOcrCall(TimeProvider time, TimeSpan budget, string id, CancellationToken caller)
    { _time = time; _budget = budget; _id = id; _caller = caller; Started = time.GetTimestamp(); }
    internal void Start()
    {
        _registration = _caller.Register(() => Cancel(false));
        var remaining = _budget - _time.GetElapsedTime(Started);
        _timer = _time.CreateTimer(_ => Cancel(false), null, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
    internal void Cancel(bool stopping)
    {
        lock (_gate)
        {
            _stopped |= stopping;
            if (!_sealed) _cancel ??= Task.Run(async () => await _source.CancelAsync().ConfigureAwait(false));
        }
    }
    internal void Check()
    {
        if (_caller.IsCancellationRequested || _stopped) throw new OperationCanceledException(_caller.IsCancellationRequested ? _caller : new CancellationToken(true));
        if (_time.GetElapsedTime(Started) >= _budget) throw new WindowsOcrException(ErrorCode.Timeout, "PROFILE_TIMEOUT", _id);
    }
    internal Exception Classify(string stage, Exception error)
    {
        if (error is OperationCanceledException) { try { Check(); } catch (Exception reason) { return reason; } }
        return WindowsOcrErrors.Map(stage, error, _id);
    }
    public async ValueTask DisposeAsync()
    {
        var errors = new List<string>();
        if (_timer != null)
            try { await _timer.DisposeAsync().ConfigureAwait(false); _timer = null; } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("TIMER", ex)); }
        try { await _registration.DisposeAsync().ConfigureAwait(false); _registrationDisposed = true; } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("CALLER", ex)); }
        Task? cancel;
        lock (_gate) { _sealed = true; cancel = _cancel; _cancel = null; }
        try { if (cancel != null) await cancel.ConfigureAwait(false); } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("CANCEL", ex)); }
        if (!_sourceDisposed)
            try { _source.Dispose(); _sourceDisposed = true; } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("SOURCE", ex)); }
        if (errors.Count != 0) throw new WindowsOcrException(ErrorCode.Unknown, "CALL_CLEANUP", _id, string.Join(';', errors));
    }
}
