using System.Threading.Channels;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Hooks;

internal sealed partial class MouseSelectionMonitor(
    NativeMouseAdapter native, IDesktopContext desktop, IMouseThreadFactory threads,
    ILogger<MouseSelectionMonitor> logger) : IMouseSelectionMonitor
{
    private readonly object _gate = new();
    private Run? _run;
    private StartAttempt? _start;
    private Task? _stop;
    private bool _disposed;
    private bool _disposing;
    private long _generation;
    private bool _running;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public long Generation { get { lock (_gate) return _generation; } }
    public event EventHandler<SelectionGestureEventArgs>? SelectionGestureDetected;
    public event EventHandler<MouseSelectionInputEventArgs>? MouseDown;
    public event EventHandler<MouseSelectionInvalidatedEventArgs>? Invalidated;

    public Task StartAsync(MouseSelectionMonitorOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options == null || options.MinDragDistancePx is < 2 or > 50 || options.ExcludedProcesses == null)
            throw InvalidOptions();
        string[] excluded = options.ExcludedProcesses.ToArray();
        if (excluded.Any(static name => name == null)) throw InvalidOptions();
        var captured = options with { ExcludedProcesses = Array.AsReadOnly(excluded) };
        StartAttempt attempt;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
            if (_start != null || _stop is { IsCompleted: false } || _run is { Revoked: true })
                throw FailureException(new("Busy", 0));
            attempt = new(captured, cancellationToken);
            _start = attempt;
            attempt.OldRun = _run;
            if (_run is { } old) RevokeLocked(old);
            else attempt.NewRun = CreateRunLocked(captured);
        }
        attempt.Registration = cancellationToken.UnsafeRegister(_ =>
        {
            // Only internal logical invalidation runs on the cancelling thread.
            lock (_gate)
            {
                if (_start != attempt) return;
                attempt.Aborted = true;
                if (_run is { } current) RevokeLocked(current);
            }
        }, null);
        _ = Task.Run(() => StartCoreAsync(attempt), CancellationToken.None);
        return attempt.Completion.Task;
    }

    private Run CreateRunLocked(MouseSelectionMonitorOptions options)
    {
        // Reserve identity and all managed ownership synchronously, before Start returns its task.
        var run = new Run(++_generation, options);
        run.Hook = NativeMouseAdapter.CreateRegistration((code, message, data) => Hook(run, code, message, data));
        _run = run;
        run.Consumer = Task.Run(() => ConsumeAsync(run));
        return run;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_start != null) _start.Aborted = true;
            if (_run is { } run) RevokeLocked(run);
            if (_stop is { IsCompleted: false }) return _stop;
            if (_start is { } start)
                return _stop = Task.Run(async () =>
                {
                    var error = await start.Settled.Task.ConfigureAwait(false);
                    if (error != null) throw error;
                });
            if (_run == null) return Task.CompletedTask;
            var current = _run;
            return _stop = Task.Run(async () =>
            {
                var cleanup = await CleanupAsync(current).ConfigureAwait(false);
                CompleteCleanupOrThrow(current, cleanup);
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_run != null || _start != null || _stop is { IsCompleted: false })
                throw FailureException(new("DisposeRequiresStop", 0));
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposing = true;
        await StopAsync().ConfigureAwait(false);
        lock (_gate) _disposed = true;
    }

    private async Task StartCoreAsync(StartAttempt attempt)
    {
        Failure? primary = null;
        Failure? cleanup = null;
        Run? current = null;
        bool accepted = false;
        try
        {
            current = attempt.OldRun;
            if (current != null)
            {
                cleanup = await CleanupAsync(current).ConfigureAwait(false);
                primary = current.Fault;
                if (cleanup != null || primary != null) return;
            }
            lock (_gate)
            {
                bool cancelled = attempt.Aborted || attempt.Token.IsCancellationRequested;
                current = attempt.NewRun;
                if (current == null && !cancelled) current = attempt.NewRun = CreateRunLocked(attempt.Options);
                if (current == null) return;
                if (cancelled)
                {
                    current.StartupFinished.TrySetResult();
                    current.Initialized.TrySetResult();
                    return;
                }
            }
            string stage = "ThreadCreate";
            try
            {
                current.Thread = threads.Create(() => MessageThread(current));
                stage = "ThreadStart";
                current.Thread.Start();
                current.ThreadStarted = true;
            }
            catch (Exception ex)
            {
                RecordFault(current, new(stage, ex.HResult));
                current.Initialized.TrySetResult();
            }
            finally { current.StartupFinished.TrySetResult(); }
            await current.Initialized.Task.ConfigureAwait(false);
            lock (_gate)
            {
                if (!attempt.Aborted && !attempt.Token.IsCancellationRequested && !current.Revoked && current.Fault == null)
                {
                    Volatile.Write(ref current.Admitted, 1);
                    _running = accepted = true;
                }
                else RevokeLocked(current);
            }
        }
        catch (Exception ex)
        {
            primary = new("Start", ex.HResult);
        }
        finally
        {
            // Unregister before committing Start, so later caller cancellation cannot stop a run.
            await attempt.Registration.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                accepted &= !attempt.Aborted && !attempt.Token.IsCancellationRequested && current is { Revoked: false };
                if (!accepted && current != null) RevokeLocked(current);
                if (accepted) _start = null; // Stop now owns cleanup, atomically with the successful commit.
            }
            if (!accepted && current != null && cleanup == null)
                cleanup = await CleanupAsync(current).ConfigureAwait(false);
            if (!accepted) primary ??= current?.Fault;
            if (!accepted && current?.Fault != null) { lock (_gate) current.FaultReported = true; }
            if (cleanup != null && primary == null && (attempt.Aborted || attempt.Token.IsCancellationRequested)) primary = new("Cancelled", 0);
            IrisException? cleanupError = cleanup == null ? null : FailureException(primary, cleanup);
            if (!accepted) { lock (_gate) _start = null; }
            attempt.Settled.TrySetResult(cleanupError);
            if (primary != null || cleanup != null) attempt.Completion.TrySetException(FailureException(primary, cleanup));
            else if (!accepted) attempt.Completion.TrySetCanceled(attempt.Token.IsCancellationRequested ? attempt.Token : new CancellationToken(true));
            else attempt.Completion.TrySetResult();
        }
    }

    private void RevokeLocked(Run run)
    {
        if (run.Revoked) return;
        run.Revoked = true;
        Volatile.Write(ref run.Admitted, 0);
        _running = false;
        run.Invalidation = new(++_generation, run.Fault == null ? null : DefaultError());
        run.Queue.Writer.TryComplete();
    }

    private void RecordFault(Run run, Failure failure)
    {
        lock (_gate)
        {
            run.Fault ??= failure;
            RevokeLocked(run);
            if (_start == null && _stop is not { IsCompleted: false })
            {
                // Observe the background fault here; Stop can still retry retained resources.
                _stop = Task.Run(async () =>
                {
                    var cleanup = await CleanupAsync(run).ConfigureAwait(false);
                    LogFailure(failure);
                    if (cleanup != null) LogFailure(cleanup);
                    CompleteCleanupOrThrow(run, cleanup);
                });
                _ = _stop.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    private void CompleteCleanupOrThrow(Run run, Failure? cleanup)
    {
        Failure? fault;
        lock (_gate)
        {
            fault = cleanup != null || !run.FaultReported ? run.Fault : null;
            if (fault != null) run.FaultReported = true;
        }
        if (fault != null || cleanup != null) throw FailureException(fault, cleanup);
    }

    private void MessageThread(Run run)
    {
        string stage = "MessageQueue";
        try
        {
            native.CreateQueue();
            run.ThreadId = native.CurrentThreadId();
            if (run.ThreadId == 0) throw new InvalidOperationException();
            stage = "LoadCursor";
            run.IBeam = native.LoadIBeam(); // Zero is an unknown cursor, conservatively rejected when required.
            run.SelfProcess = native.CurrentProcessId();
            if (run.SelfProcess == 0) throw new InvalidOperationException();
            stage = "Install";
            native.Install(run.Hook!);
            if (run.Hook!.Handle == 0) { RecordFault(run, new(stage, native.LastError())); return; }
            run.Initialized.TrySetResult();
            stage = "MessagePump";
            int result;
            while ((result = native.PumpMessage()) > 0) { }
            if (result < 0 || !run.Revoked) RecordFault(run, new(stage, result < 0 ? native.LastError() : 0));
        }
        catch (Exception ex) { RecordFault(run, new(stage, ex.HResult)); }
        finally
        {
            run.Initialized.TrySetResult();
            run.ThreadExited.TrySetResult();
        }
    }

    private async Task<Failure?> CleanupAsync(Run run)
    {
        // Called by the one lifecycle owner (Start, Stop or runtime-failure cleanup).
        await run.StartupFinished.Task.ConfigureAwait(false);
        await run.Initialized.Task.ConfigureAwait(false);
        Failure? failure = null;
        if (run.Hook is { Handle: not 0, Unhooked: false } hook)
        {
            try
            {
                if (native.Unhook(hook)) hook.Unhooked = true;
                else failure = new("Unhook", native.LastError());
            }
            catch (Exception ex) { failure = new("Unhook", ex.HResult); }
        }
        bool canExit = run.Hook is not { Handle: not 0, Unhooked: false };
        if (canExit && run.ThreadStarted && !run.ThreadExited.Task.IsCompleted && !run.QuitPosted)
        {
            try
            {
                if (native.PostQuit(run.ThreadId)) run.QuitPosted = true;
                else failure = new("PostQuit", native.LastError());
            }
            catch (Exception ex) { failure = new("PostQuit", ex.HResult); }
        }
        await run.Consumer.ConfigureAwait(false);
        if (run.ThreadStarted && (run.QuitPosted || run.ThreadExited.Task.IsCompleted))
        {
            await run.ThreadExited.Task.ConfigureAwait(false);
            // ThreadExited is signalled in finally; Join also waits the actual managed thread return.
            run.Thread!.Join();
        }
        if (failure != null) return failure;
        while (Volatile.Read(ref run.Callbacks) != 0) await run.CallbacksIdle.Reader.ReadAsync().ConfigureAwait(false);
        run.Hook = null; // Delegate stays rooted through unhook, callbacks and real thread exit.
        lock (_gate) { if (_run == run) _run = null; }
        return null;
    }

    private sealed class StartAttempt(MouseSelectionMonitorOptions options, CancellationToken token)
    {
        internal readonly MouseSelectionMonitorOptions Options = options;
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<IrisException?> Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal bool Aborted;
        internal Run? OldRun;
        internal Run? NewRun;
    }

    private sealed class Run(long generation, MouseSelectionMonitorOptions options)
    {
        internal readonly long Generation = generation;
        internal readonly MouseSelectionMonitorOptions Options = options;
        internal readonly Channel<QueuedMouseSample> Queue = Channel.CreateBounded<QueuedMouseSample>(new BoundedChannelOptions(256)
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait,
        });
        internal readonly TaskCompletionSource Initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StartupFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ThreadExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Channel<byte> CallbacksIdle = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        internal MouseHookRegistration? Hook;
        internal Task Consumer = Task.CompletedTask;
        internal Thread? Thread;
        internal uint ThreadId;
        internal uint SelfProcess;
        internal nint IBeam;
        internal bool ThreadStarted;
        internal bool QuitPosted;
        internal volatile bool Revoked;
        internal int Admitted;
        internal int Callbacks;
        internal long Epoch;
        internal int SnapshotFailure;
        internal MouseSelectionInvalidatedEventArgs? Invalidation;
        internal Failure? Fault;
        internal bool FaultReported;
    }

    private sealed record Failure(string Stage, int Code);
    private static IrisException InvalidOptions() => new(ErrorCode.InvalidArgument, ErrorMessages.GetDefault(ErrorCode.InvalidArgument));
    private static ErrorInfo DefaultError() => new(ErrorCode.HookInstallFailed, ErrorMessages.GetDefault(ErrorCode.HookInstallFailed));
    private static IrisException FailureException(Failure? primary, Failure? cleanup = null) =>
        new(ErrorCode.HookInstallFailed, ErrorMessages.GetDefault(ErrorCode.HookInstallFailed), retryable: true,
            detail: $"phase={primary?.Stage ?? "None"};code={primary?.Code ?? 0};cleanup={cleanup?.Stage ?? "None"};cleanupCode={cleanup?.Code ?? 0}");
    private void LogFailure(Failure failure)
    {
        try { LogDiagnostic(logger, failure.Stage, failure.Code); }
        catch (Exception) { /* A logging sink cannot abandon native ownership. */ }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Mouse monitor phase={Phase}; code={Code}")]
    private static partial void LogDiagnostic(ILogger logger, string phase, int code);
}
