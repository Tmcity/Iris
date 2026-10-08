namespace Iris.Platform.Windows.SystemIntegration;

/// <summary>Cancellation requests are nonblocking; cleanup joins timer and actual callbacks, including failures.</summary>
internal sealed class IpcCancellation : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _caller;
    private readonly ITimer? _timer;
    private Task? _cancel;
    private bool _joined, _disposed;
    internal CancellationToken Token => _source.Token;
    internal IpcCancellation(TimeProvider time, TimeSpan? deadline = null, CancellationToken caller = default)
    {
        _caller = caller.UnsafeRegister(static value => ((IpcCancellation)value!).Request(), this);
        if (deadline is { } duration) _timer = time.CreateTimer(static value => ((IpcCancellation)value!).Request(), this, duration, Timeout.InfiniteTimeSpan);
    }
    internal void Request()
    {
        lock (_gate) { if (!_disposed) _cancel ??= _source.CancelAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _caller.DisposeAsync().ConfigureAwait(false);
        if (_timer != null) await _timer.DisposeAsync().ConfigureAwait(false);
        Task? cancel; lock (_gate) { _disposed = true; cancel = _cancel; }
        if (cancel != null && !_joined)
        {
            try { await cancel.ConfigureAwait(false); }
            finally { _joined = true; }
        }
        lock (_gate) { _disposed = true; _source.Dispose(); }
    }
}
