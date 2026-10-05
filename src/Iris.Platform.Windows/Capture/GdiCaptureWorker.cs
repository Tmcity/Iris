using System.Collections.Concurrent;
using Iris.Core.Platform;

namespace Iris.Platform.Windows.Capture;

internal interface ICaptureThreadFactory { Thread Create(ThreadStart start); }
internal sealed class CaptureThreadFactory : ICaptureThreadFactory
{
    public Thread Create(ThreadStart start) => new(start) { IsBackground = true, Name = "Iris.GdiCapture" };
}

internal sealed class GdiCaptureWorker : IDisposable
{
    private readonly NativeGdiCaptureAdapter _native;
    private readonly IDesktopContext _desktop;
    private readonly TimeProvider _time;
    private readonly BlockingCollection<Action> _commands = new(1);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private GdiCaptureSession? _session;
    private bool _started, _joined;

    internal GdiCaptureWorker(NativeGdiCaptureAdapter native, IDesktopContext desktop, TimeProvider time, ICaptureThreadFactory factory)
    {
        _native = native; _desktop = desktop; _time = time;
        try { _thread = factory.Create(Run); }
        catch (Exception ex) { _commands.Dispose(); throw ScreenCaptureErrors.Combine("WORKER_CREATE", ex, [], false); }
    }
    internal void Start()
    {
        try { _thread.Start(); _started = true; }
        catch (Exception ex) { _commands.Dispose(); throw ScreenCaptureErrors.Combine("WORKER_START", ex, [], false); }
    }
    private void Run()
    {
        try { foreach (var command in _commands.GetConsumingEnumerable()) command(); }
        finally { _exited.TrySetResult(); }
    }
    private Task<T> InvokeAsync<T>(Func<T> operation)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = done.Task;
        if (!_commands.TryAdd(() =>
        {
            // The blocking enumerator can retain its last delegate while idle. Empty its captured references.
            var completion = done;
            var work = operation;
            done = null!; operation = null!;
            try { completion.TrySetResult(work()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) throw ScreenCaptureErrors.Fail("WORKER_BUSY");
        return task;
    }
    internal Task<CapturedFrame> CaptureAsync(Action check, Func<bool> cancelled) => InvokeAsync(() => Capture(check, cancelled));
    private CapturedFrame Capture(Action check, Func<bool> cancelled)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            check();
            _session = new(_native);
            Exception? body = null;
            CapturedFrame? result = null;
            bool changed = false;
            string stage = "DPI_ENTER";
            try
            {
                _session.EnterDpi(); check();
                stage = "MONITORS_BEFORE";
                var before = ScreenPixelLayout.Snapshot(_desktop.GetMonitors()); check();
                stage = "CAPTURE";
                byte[] pixels = _session.Capture(before.Bounds, before.Monitors, check);
                var capturedAt = _time.GetUtcNow().ToUniversalTime(); check();
                stage = "MONITORS_AFTER";
                var after = ScreenPixelLayout.Snapshot(_desktop.GetMonitors()); check();
                changed = !ScreenPixelLayout.SameTopology(before.Monitors, after.Monitors);
                if (!changed) result = new(pixels, before.Bounds.Width, before.Bounds.Height, checked(before.Bounds.Width * 4), before.Bounds, before.Monitors, capturedAt);
            }
            catch (Exception ex) { body = ex; }
            var cleanup = Cleanup();
            if (cleanup.Count != 0 || body != null && (body is not OperationCanceledException || !cancelled()))
                throw ScreenCaptureErrors.Combine(stage, body, cleanup, cancelled());
            if (body is OperationCanceledException) throw body;
            check();
            if (!changed) return result!;
        }
        throw ScreenCaptureErrors.Fail("TOPOLOGY_CHANGED_TWICE");
    }
    private IReadOnlyList<string> Cleanup()
    {
        if (_session == null) return [];
        var errors = _session.Cleanup();
        if (!_session.HasResources) _session = null;
        return errors;
    }
    internal Task<IReadOnlyList<string>> RetryCleanupAsync() => InvokeAsync(Cleanup);
    internal async Task StopAsync()
    {
        if (_joined) return;
        _commands.CompleteAdding();
        await _exited.Task.ConfigureAwait(false);
        _thread.Join(); // The finally signal alone is not proof of actual thread exit.
        _joined = true; _commands.Dispose();
    }
    public void Dispose()
    {
        if (_started && !_joined) throw new InvalidOperationException("Capture worker must be joined asynchronously.");
        if (!_started) _commands.Dispose();
    }
}
