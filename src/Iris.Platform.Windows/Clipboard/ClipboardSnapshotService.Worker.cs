using Iris.Core.Errors;

namespace Iris.Platform.Windows.Clipboard;

internal sealed partial class ClipboardSnapshotService
{
    internal const int QueueCapacity = 32;
    private readonly object _gate = new();
    private readonly LinkedList<Action> _commands = new();
    private readonly HashSet<Task> _admittedTasks = [];
    private readonly CancellationTokenSource _shutdown = new();
    private AutoResetEvent? _wake;
    private Thread? _thread;
    private Task? _cleanupAttempt;
    private Task? _stopTask;
    private Task? _joinTask;
    private bool _stopping;
    private bool _blocked;
    private IrisException? _blockedError;
    private bool _pumpFailed;
    private bool _exitRequested;
    private bool _joined;
    private bool _disposed;

    private Task<T> Submit<T>(Func<CancellationToken, T> operation, CancellationToken token, ClipboardSnapshot.Lease? lease = null)
    {
        CancellationTokenSource? linked = null;
        try
        {
            token.ThrowIfCancellationRequested();
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            var cancellation = linked;
            var operationToken = cancellation.Token;
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifetimeGate = new object();
            CancellationTokenRegistration registration = default;
            bool finished = false;
            void Finish(Action publish)
            {
                CancellationTokenRegistration registered;
                lock (lifetimeGate) { finished = true; registered = registration; }
                registered.Dispose();
                lease?.Dispose();
                cancellation.Dispose();
                publish();
            }
            LinkedListNode<Action> node;
            lock (_gate)
            {
                if (_stopping || _disposed) throw new InvalidOperationException("Clipboard snapshot service is stopped.");
                if (_blocked) throw _blockedError ?? Safe("CLEANUP_REQUIRED", 0);
                if (_commands.Count >= QueueCapacity) throw new IrisException(ErrorCode.ClipboardBusy, "QUEUE_FULL");
                EnsureWorker();
                _admittedTasks.RemoveWhere(task => task.IsCompleted);
                _admittedTasks.Add(completion.Task);
                node = _commands.AddLast(() =>
                {
                    T? result = default;
                    Exception? body = null;
                    bool executing = false;
                    var cleanup = new List<string>();
                    try
                    {
                        CheckCancellation(operationToken);
                        lock (_gate) { if (_blocked) throw _blockedError ?? Safe("CLEANUP_REQUIRED", 0); }
                        executing = true;
                        _commitStarted = false;
                        result = operation(operationToken);
                    }
                    catch (Exception ex) { body = ex; }
                    finally
                    {
                        if (executing) CleanupResources(cleanup);
                    }
                    if (cleanup.Count != 0)
                    {
                        if (result is ClipboardSnapshotResult capture) capture.Snapshot?.Dispose();
                        string bodyCode = body == null ? "NONE" : Describe(body);
                        var failure = new IrisException(ErrorCode.CaptureFailed, $"CLIPBOARD_CLEANUP body={bodyCode} stages={string.Join(',', cleanup)}");
                        lock (_gate) { _blocked = true; _blockedError = failure; }
                        Finish(() => completion.TrySetException(failure));
                    }
                    else if (body is OperationCanceledException canceled) Finish(() => completion.TrySetCanceled(canceled.CancellationToken));
                    else if (body is NativeFailure known) Finish(() => completion.TrySetException(known));
                    else if (body != null) Finish(() => completion.TrySetException(Safe("NATIVE", body.HResult)));
                    else Finish(() =>
                    {
                        lock (_gate)
                        {
                            if (!_commitStarted && (_stopping || operationToken.IsCancellationRequested))
                            {
                                if (result is ClipboardSnapshotResult capture) capture.Snapshot?.Dispose();
                                completion.TrySetCanceled(operationToken);
                            }
                            else completion.TrySetResult(result!);
                        }
                    });
                });
                _wake!.Set();
            }
            var registered = operationToken.Register(() =>
            {
                lock (_gate)
                {
                    if (node.List == null) return; // Active native work completes its real finally.
                    _commands.Remove(node);
                }
                Finish(() => completion.TrySetCanceled(operationToken));
            });
            lock (lifetimeGate) { if (!finished) registration = registered; }
            if (finished) registered.Dispose();
            return completion.Task;
        }
        catch
        {
            linked?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private void CheckCancellation(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException("Clipboard snapshot service stopped.", token);
        }
    }

    private void EnsureWorker()
    {
        if (_thread != null) return;
        _wake = new AutoResetEvent(false);
        var thread = new Thread(WorkerMain) { IsBackground = true, Name = "Iris clipboard snapshots" };
        thread.SetApartmentState(ApartmentState.MTA);
        try { thread.Start(); _thread = thread; }
        catch (Exception ex)
        {
            _wake.Dispose();
            _wake = null;
            throw Safe("THREAD_START", ex.HResult);
        }
    }

    private void WorkerMain()
    {
        try { EnsureOwner(); }
        catch (Exception ex) { lock (_gate) { _blocked = true; _blockedError = Safe("CREATE_OWNER", ex.HResult); } }
        while (true)
        {
            Action? command;
            lock (_gate)
            {
                if (_exitRequested) return;
                command = _commands.First?.Value;
                if (_commands.First != null) _commands.RemoveFirst();
            }
            if (command != null) command();
            lock (_gate) { if (_exitRequested) return; }
            if (!_frameOpen && !_pumpFailed)
            {
                try { _native.PumpMessages(); }
                catch (Exception ex) { lock (_gate) { _blocked = true; _pumpFailed = true; _blockedError = Safe("PUMP", ex.HResult); } }
            }
            lock (_gate) { if (_exitRequested) return; if (_commands.Count != 0) continue; }
            try
            {
                if (_pumpFailed || _frameOpen) _wake!.WaitOne();
                else _native.WaitForWork(_wake!);
            }
            catch (Exception ex)
            {
                lock (_gate) { _blocked = true; _pumpFailed = true; _blockedError = Safe("MESSAGE_WAIT", ex.HResult); }
            }
        }
    }

    private void EnsureOwner()
    {
        if (_window != 0) return;
        _window = _native.CreateOwnerWindow();
        Check(_window != 0, "CREATE_OWNER");
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Cancellation cannot withdraw an already requested physical stop.
        lock (_gate)
        {
            _stopping = true;
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_joined || _thread == null) return Task.CompletedTask;
            return _stopTask = Task.Run(StopCoreAsync, CancellationToken.None);
        }
    }

    private async Task StopCoreAsync()
    {
        Exception? cancelError = null;
        try { _shutdown.Cancel(); }
        catch (Exception ex) { cancelError = Safe("CANCEL_CALLBACK", ex.HResult); }
        try
        {
            while (true)
            {
                await RetryCleanupAsync().ConfigureAwait(false);
                lock (_gate) { if (_exitRequested) break; }
            }
            await JoinAsync().ConfigureAwait(false);
        }
        finally
        {
            Task[] accepted;
            lock (_gate) accepted = _admittedTasks.ToArray();
            // Each operation reports its own result/error. Observe all of them even
            // if physical cleanup failed, including concurrent queued cancellation.
            await Task.WhenAll(accepted.Select(task => task.ContinueWith(
                completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))).ConfigureAwait(false);
        }
        if (cancelError != null) throw cancelError;
    }

    public async Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        Task cleanup;
        lock (_gate)
        {
            if (_thread == null || _joined) return;
            if (_exitRequested) cleanup = Task.CompletedTask;
            else if (_cleanupAttempt is { IsCompleted: false }) cleanup = _cleanupAttempt;
            else
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cleanup = _cleanupAttempt = completion.Task;
                _commands.AddLast(() =>
                {
                    var failures = new List<string>();
                    CleanupResources(failures, retry: true);
                    bool stopping;
                    lock (_gate) stopping = _stopping;
                    if (!_frameOpen && _blocks.Count == 0)
                    {
                        if (stopping && _window != 0)
                            Attempt(() => { Check(_native.DestroyWindow(_window), "DESTROY_OWNER"); _window = 0; }, "DESTROY", failures);
                        else if (!stopping)
                        {
                            Attempt(EnsureOwner, "CREATE_OWNER", failures);
                            Attempt(_native.PumpMessages, "PUMP", failures);
                        }
                    }
                    lock (_gate)
                    {
                        _blocked = failures.Count != 0 || _frameOpen || _blocks.Count != 0;
                        if (!_blocked)
                        {
                            _blockedError = null;
                            _pumpFailed = false;
                            if (stopping && _window == 0) _exitRequested = true;
                        }
                    }
                    if (_blocked)
                    {
                        var failure = new IrisException(ErrorCode.CaptureFailed, $"CLIPBOARD_RETRY stages={string.Join(',', failures)}");
                        lock (_gate) _blockedError = failure;
                        completion.TrySetException(failure);
                    }
                    else completion.TrySetResult();
                });
                _wake!.Set();
            }
        }
        await cleanup.ConfigureAwait(false);
        bool exiting;
        lock (_gate) exiting = _exitRequested;
        if (exiting) await JoinAsync().ConfigureAwait(false);
    }

    private Task JoinAsync()
    {
        lock (_gate)
        {
            return _joinTask ??= Task.Run(() =>
            {
                _thread!.Join();
                AutoResetEvent? wake;
                lock (_gate) { wake = _wake; _wake = null; }
                wake?.Dispose();
                lock (_gate) _joined = true;
            }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_thread != null && !_joined) throw new InvalidOperationException("Asynchronous clipboard shutdown is required.");
            _stopping = true;
            _disposed = true;
        }
        _wake?.Dispose();
        _shutdown.Dispose();
    }
}
