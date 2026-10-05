using Iris.Core.Platform;

namespace Iris.Platform.Windows.Hooks;

internal sealed partial class MouseSelectionMonitor
{
    private nint Hook(Run run, int code, nuint message, nint data)
    {
        Interlocked.Increment(ref run.Callbacks);
        try
        {
            if (code >= 0 && Volatile.Read(ref run.Admitted) != 0)
            {
                try
                {
                    if (native.Read(message, data, run.IBeam) is { } sample)
                    {
                        long epoch = Volatile.Read(ref run.Epoch);
                        if (!run.Queue.Writer.TryWrite(new(sample, epoch))) Interlocked.Increment(ref run.Epoch);
                    }
                }
                catch (Exception ex) { MarkSnapshotFailure(run, ex.HResult); }
            }
            // Even a malformed snapshot must continue the input chain. No exception may escape to Win32.
            try { return native.CallNext(code, message, data); }
            catch (Exception ex) { MarkSnapshotFailure(run, ex.HResult); return 0; }
        }
        finally
        {
            if (Interlocked.Decrement(ref run.Callbacks) == 0 && run.Revoked) run.CallbacksIdle.Writer.TryWrite(0);
        }
    }

    private static void MarkSnapshotFailure(Run run, int code)
    {
        Interlocked.Exchange(ref run.SnapshotFailure, code);
        long epoch = Interlocked.Increment(ref run.Epoch);
        run.Queue.Writer.TryWrite(new(default, epoch)); // Wake the worker even when the failed event was the only input.
    }

    private async Task ConsumeAsync(Run run)
    {
        var recognizer = new GestureRecognizer(run.Options);
        long epoch = 0;
        uint? lastTime = null;
        ClickMetrics metrics = default;
        bool dirty = false;
        void Reset()
        {
            recognizer.Reset();
            lastTime = null;
            if (!dirty) Publish(run, Invalidated, new MouseSelectionInvalidatedEventArgs(run.Generation), epoch);
            dirty = true;
        }
        try
        {
            await foreach (var queued in run.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref run.Admitted) == 0) break;
                long latestEpoch = Volatile.Read(ref run.Epoch);
                int snapshotFailure = Interlocked.Exchange(ref run.SnapshotFailure, 0);
                if (snapshotFailure != 0) LogFailure(new("Snapshot", snapshotFailure));
                if (epoch != latestEpoch) { epoch = latestEpoch; DiscardLostEpoch(run, epoch); Reset(); }
                if (queued.Epoch != epoch) continue;
                var sample = queued.Sample;
                if (!Fresh(sample) || (lastTime is uint last && unchecked(sample.Time - last) > int.MaxValue))
                {
                    Reset();
                    continue;
                }
                lastTime = sample.Time;
                if (sample.IsDown) Publish(run, MouseDown, new MouseSelectionInputEventArgs(sample.Point, run.Generation), epoch);
                if (sample.Injected || (sample.IsDown && sample.Message != MouseMessage.LeftDown) ||
                    sample.Message is MouseMessage.Wheel or MouseMessage.HorizontalWheel)
                {
                    recognizer.Reset();
                    continue;
                }
                ForegroundAppInfo? app = null;
                if (sample.Message is MouseMessage.LeftDown or MouseMessage.LeftUp)
                {
                    try
                    {
                        app = desktop.GetForegroundApp();
                        if (app == null || app.WindowHandle == 0 || app.ProcessId <= 0 || (uint)app.ProcessId == run.SelfProcess ||
                            sample.Source != new MouseSource(app.WindowHandle, (uint)app.ProcessId) || !ValidProcess(app.ProcessName) ||
                            run.Options.ExcludedProcesses.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase))
                        {
                            recognizer.Reset();
                            continue;
                        }
                        app = app with { WindowTitle = null };
                        if (sample.Message == MouseMessage.LeftDown)
                        {
                            metrics = default;
                            try { metrics = native.Metrics(desktop.GetMonitorAt(sample.Point).DpiScale); }
                            catch (Exception ex) { LogFailure(new("Metrics", ex.HResult)); }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure(new("Source", ex.HResult));
                        recognizer.Reset();
                        continue;
                    }
                }
                latestEpoch = Volatile.Read(ref run.Epoch);
                if (epoch != latestEpoch) { epoch = latestEpoch; DiscardLostEpoch(run, epoch); Reset(); continue; }
                if (!Fresh(sample)) { Reset(); continue; }
                if (sample.Message == MouseMessage.LeftDown) dirty = false;
                var gesture = recognizer.Accept(sample, run.Generation, metrics);
                if (gesture != null)
                    Publish(run, SelectionGestureDetected, new SelectionGestureEventArgs(sample.Point, gesture.Value, app) { Generation = run.Generation }, epoch);
            }
        }
        catch (Exception ex) { RecordFault(run, new("Consumer", ex.HResult)); }
        finally
        {
            if (run.Invalidation is { } invalidation) PublishInvalidation(invalidation);
        }
    }

    private static void DiscardLostEpoch(Run run, long epoch)
    {
        // Free lost residuals before notifying recovery; leave already-queued clean input intact.
        while (run.Queue.Reader.TryPeek(out var next) && next.Epoch != epoch) run.Queue.Reader.TryRead(out _);
    }

    private bool Fresh(MouseSample sample) => unchecked(native.TickCount() - sample.Time) <= 250;
    private static bool ValidProcess(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0;

    private void Publish<T>(Run run, EventHandler<T>? handlers, T args, long epoch) where T : EventArgs
    {
        if (handlers == null) return;
        foreach (EventHandler<T> subscriber in handlers.GetInvocationList())
        {
            lock (_gate)
            {
                if (_run != run || run.Revoked || run.Generation != _generation || epoch != Volatile.Read(ref run.Epoch)) return;
                // The consumer owns this acquired publication until the callback returns.
            }
            try { subscriber(this, args); }
            catch (Exception ex) { LogFailure(new("Subscriber", ex.HResult)); }
        }
    }

    private void PublishInvalidation(MouseSelectionInvalidatedEventArgs args)
    {
        if (Invalidated is not { } handlers) return;
        foreach (EventHandler<MouseSelectionInvalidatedEventArgs> subscriber in handlers.GetInvocationList())
        {
            try { subscriber(this, args); }
            catch (Exception ex) { LogFailure(new("InvalidatedSubscriber", ex.HResult)); }
        }
    }
}
