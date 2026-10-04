using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Clipboard;

namespace Iris.Platform.Windows.Selection;

internal sealed class CompositeSelectionReader(
    UiaSelectionReader uia, ClipboardSnapshotService clipboard, KeyboardSimulator keyboard,
    IDesktopContext desktop, CopySafetyPolicy safety, TimeProvider timeProvider) : ISelectionTextReader, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<SelectionReadResult?>? _readTask;
    private Task? _retryTask;
    private Task? _stopTask;
    private Operation? _current;
    private Operation? _pending;
    private bool _stopped;
    private bool _fullyStopped;
    private bool _used;
    private bool _disposed;
    private bool _keyboardStopped;
    private bool _uiaStopped;

    public Task<SelectionReadResult?> ReadAsync(SelectionReadOptions options, CancellationToken cancellationToken)
    {
        var frozen = Freeze(options);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopped) throw new InvalidOperationException("Selection reader stopped.");
            if (_readTask is { IsCompleted: false } || _retryTask is { IsCompleted: false }) throw Failure("BUSY");
            var op = new Operation(frozen, cancellationToken);
            _current = op;
            _used = true;
            return _readTask = Task.Run(() => ReadCoreAsync(op), CancellationToken.None);
        }
    }

    private static SelectionReadOptions Freeze(SelectionReadOptions options)
    {
        if (options?.Order == null || options.ClipboardUnsafeProcesses == null || options.ClipboardTimeoutMs is < 100 or > 2000)
            throw new IrisException(ErrorCode.InvalidArgument, "SELECTION_OPTIONS_INVALID");
        var order = options.Order.ToArray();
        var excluded = options.ClipboardUnsafeProcesses.ToArray();
        if (order.Any(method => method is not SelectionReadMethod.UIAutomation and not SelectionReadMethod.Clipboard) || excluded.Any(name => name == null))
            throw new IrisException(ErrorCode.InvalidArgument, "SELECTION_OPTIONS_INVALID");
        return new(order.Distinct().ToArray(), options.RestoreClipboard, options.ClipboardTimeoutMs, excluded);
    }

    private async Task<SelectionReadResult?> ReadCoreAsync(Operation op)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(op.Caller, _shutdown.Token);
        Exception? body = null;
        Exception? cleanup = null;
        SelectionReadResult? result = null;
        bool started = false;
        try
        {
            await CleanupPendingAsync().ConfigureAwait(false);
            started = true;
            linked.Token.ThrowIfCancellationRequested();
            if (op.Options.Order.Count != 0)
            {
                op.Source = CaptureSource();
                if (IsConfirmedSource(op.Source))
                    result = await ReadMethodsAsync(op, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { body = ex; }
        finally
        {
            if (started)
            {
                try { await CleanupOperationAsync(op).ConfigureAwait(false); }
                catch (Exception ex) { cleanup = ex; lock (_gate) _pending = op; }
            }
        }
        if (cleanup != null) throw Failure("CLEANUP", cleanup, body);
        if (body is OperationCanceledException) throw new OperationCanceledException(op.Caller.IsCancellationRequested ? op.Caller : linked.Token);
        if (body != null) throw Failure("READ", body, code: body is IrisException { Code: ErrorCode.InputTooLong } ? ErrorCode.InputTooLong : ErrorCode.CaptureFailed);
        if (result != null)
        {
            try
            {
                if (!IsSourceCurrent(op) || (op.Permit != null && !IsFocusCurrent(op))) result = null;
            }
            catch (Exception ex) { throw Failure("FINAL_SOURCE", ex); }
        }
        lock (_gate)
        {
            op.Caller.ThrowIfCancellationRequested();
            if (_stopped || !ReferenceEquals(_current, op)) throw new OperationCanceledException(linked.Token);
            return result;
        }
    }

    private async Task<SelectionReadResult?> ReadMethodsAsync(Operation op, CancellationToken token)
    {
        foreach (var method in op.Options.Order)
        {
            token.ThrowIfCancellationRequested();
            if (!IsSourceCurrent(op)) return null;
            if (op.Permit != null && !IsFocusCurrent(op)) return null;
            string? text;
            if (method == SelectionReadMethod.UIAutomation)
            {
                var result = await uia.ReadAsync(op.Source!.WindowHandle, checked((uint)op.Source.ProcessId), token).ConfigureAwait(false);
                switch (result.Status)
                {
                    case UiaSelectionStatus.Sensitive:
                    case UiaSelectionStatus.TargetChanged: return null;
                    case UiaSelectionStatus.TooLong: throw Failure("TOO_LONG", code: ErrorCode.InputTooLong);
                    case UiaSelectionStatus.Busy: throw Failure("UIA_BUSY");
                    case UiaSelectionStatus.Success: text = result.Text; break;
                    default: continue;
                }
            }
            else text = await ReadClipboardAsync(op, token).ConfigureAwait(false);
            if (op.TargetChanged) return null;
            if (text?.Length > 65_536) throw Failure("TOO_LONG", code: ErrorCode.InputTooLong);
            if (!string.IsNullOrWhiteSpace(text)) return new(text, method, op.Source);
        }
        return null;
    }

    private async Task<string?> ReadClipboardAsync(Operation op, CancellationToken token)
    {
        if (op.Options.ClipboardUnsafeProcesses.Contains(op.Source!.ProcessName, StringComparer.OrdinalIgnoreCase)) return null;
        try { op.Permit = safety.Capture(op.Source); }
        catch (Exception) { return null; } // Pure query failure is not affirmative copy permission.
        if (op.Permit == null) return null;
        bool Guard() => !token.IsCancellationRequested && !IsStopped() && IsSourceCurrent(op) && IsFocusCurrent(op);
        var copy = await keyboard.CopyWithPreparationAsync(new(op.Source.WindowHandle, checked((uint)op.Source.ProcessId)), async preparationToken =>
        {
            if (!Guard()) return false;
            ClipboardVersion version;
            if (op.Options.RestoreClipboard)
            {
                var capture = await clipboard.CaptureSnapshotAsync(preparationToken).ConfigureAwait(false);
                op.Snapshot = capture.Snapshot;
                if (capture.Status != ClipboardSnapshotStatus.Success) return false;
                version = capture.Version;
            }
            else version = await clipboard.ReadVersionAsync(preparationToken).ConfigureAwait(false);
            if (!Guard()) return false;
            var clear = await clipboard.TryClearForSelectionAsync(version, op.Clear, preparationToken).ConfigureAwait(false);
            return clear.Status == ClipboardSnapshotStatus.Applied;
        }, Guard, token).ConfigureAwait(false);
        if (copy.Outcome == KeyboardCopyOutcome.Busy) throw Failure("KEYBOARD_BUSY");
        if (copy.Outcome == KeyboardCopyOutcome.Failed)
        {
            if (copy.MayHaveCopied && !keyboard.HasPendingRelease)
                await ObserveClipboardAsync(op, false, token).ConfigureAwait(false);
            throw Failure("KEYBOARD_FAILED");
        }
        if (copy.Outcome != KeyboardCopyOutcome.Injected) return null;
        return await ObserveClipboardAsync(op, true, token).ConfigureAwait(false);
    }

    private async Task<string?> ObserveClipboardAsync(Operation op, bool readText, CancellationToken token)
    {
        if (op.Clear.Version == null) return null;
        long start = timeProvider.GetTimestamp();
        bool Expired() => timeProvider.GetElapsedTime(start).TotalMilliseconds >= op.Options.ClipboardTimeoutMs;
        while (!Expired())
        {
            token.ThrowIfCancellationRequested();
            if (!IsSourceCurrent(op) || !IsFocusCurrent(op)) return null;
            var version = await clipboard.ReadVersionAsync(token).ConfigureAwait(false);
            if (Expired()) return null;
            if (version != op.Clear.Version.Value)
            {
                if (version.Owner == 0 || version.OwnerProcessId != op.Source!.ProcessId) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeProvider, token).ConfigureAwait(false);
                if (Expired()) return null;
                var stable = await clipboard.ReadVersionAsync(token).ConfigureAwait(false);
                if (Expired()) return null;
                if (stable == version)
                {
                    if (!IsSourceCurrent(op) || !IsFocusCurrent(op)) return null;
                    op.Candidate = version;
                    if (!readText) return null;
                    var text = await clipboard.ReadTextIfVersionAsync(version, token).ConfigureAwait(false);
                    if (Expired()) return null;
                    if (text.Status == ClipboardTextStatus.TooLong) throw Failure("TOO_LONG", code: ErrorCode.InputTooLong);
                    if (text.Status == ClipboardTextStatus.Changed) return null;
                    return text.Status == ClipboardTextStatus.Success ? text.Text : null;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(15), timeProvider, token).ConfigureAwait(false);
        }
        return null;
    }

    private bool IsSourceCurrent(Operation op)
    {
        var now = CaptureSource();
        var source = op.Source;
        bool valid = now != null && source != null && now.WindowHandle == source.WindowHandle && now.ProcessId == source.ProcessId
            && string.Equals(now.ProcessName, source.ProcessName, StringComparison.OrdinalIgnoreCase);
        if (!valid) op.TargetChanged = true;
        return valid;
    }
    private ForegroundAppInfo? CaptureSource()
    {
        try { return desktop.GetForegroundApp(); }
        catch (Exception) { return null; } // An unreadable source is not a new target.
    }
    private bool IsConfirmedSource(ForegroundAppInfo? source)
    {
        if (source is not { WindowHandle: not 0, ProcessId: > 0 } || string.IsNullOrWhiteSpace(source.ProcessName)) return false;
        try { return !safety.IsOwnProcess(source.ProcessId); }
        catch (Exception) { return false; }
    }
    private bool IsFocusCurrent(Operation op)
    {
        bool valid = op.Permit != null && safety.IsValid(op.Source!, op.Permit.Value);
        if (!valid) op.TargetChanged = true;
        return valid;
    }
    private bool IsStopped() { lock (_gate) return _stopped; }

    private Task CleanupOperationAsync(Operation op)
    {
        lock (_gate)
        {
            if (op.CleanupTask is { IsCompleted: false }) return op.CleanupTask;
            return op.CleanupTask = Task.Run(() => CleanupCoreAsync(op), CancellationToken.None);
        }
    }

    private async Task CleanupCoreAsync(Operation op)
    {
        var failures = new List<string>();
        await AttemptAsync(keyboard.RetryCleanupAsync, "KEYS", failures).ConfigureAwait(false);
        await AttemptAsync(() => uia.RetryCleanupAsync(), "UIA", failures).ConfigureAwait(false);
        await AttemptAsync(() => clipboard.RetryCleanupAsync(), "CLIPBOARD", failures).ConfigureAwait(false);
        if (failures.Count != 0) throw new IrisException(ErrorCode.CaptureFailed, $"SELECTION_CLEANUP {string.Join(',', failures)}");
        if (op.Snapshot != null && op.Clear.Written && !op.Restored)
        {
            if (!op.Restore.Applied)
            {
                var expected = op.Restore.Written ? op.Restore.Version : op.Candidate ?? op.Clear.Version;
                if (expected == null) throw Failure("RESTORE_VERSION_UNKNOWN");
                var restore = await clipboard.TryRestoreForSelectionAsync(op.Snapshot, expected.Value, op.Restore, CancellationToken.None).ConfigureAwait(false);
                if (restore.Status is not ClipboardSnapshotStatus.Applied and not ClipboardSnapshotStatus.Changed) throw Failure("RESTORE");
            }
            op.Restored = true;
        }
        op.Snapshot?.Dispose();
        op.Snapshot = null;
        lock (_gate) { if (ReferenceEquals(_pending, op)) _pending = null; }
    }

    private async Task CleanupPendingAsync()
    {
        Operation? pending;
        lock (_gate) pending = _pending;
        if (pending != null) await CleanupOperationAsync(pending).ConfigureAwait(false);
    }

    public Task RetryCleanupAsync()
    {
        lock (_gate)
        {
            if (_retryTask is { IsCompleted: false }) return _retryTask;
            var active = _readTask;
            return _retryTask = Task.Run(async () =>
            {
                if (active != null) await ObserveTaskAsync(active).ConfigureAwait(false);
                await CleanupPendingAsync().ConfigureAwait(false);
            }, CancellationToken.None);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopped = true;
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_fullyStopped) return Task.CompletedTask;
            return _stopTask = Task.Run(StopCoreAsync, CancellationToken.None);
        }
    }

    private async Task StopCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task? active;
        Task? retry;
        lock (_gate) { active = _readTask; retry = _retryTask; }
        if (active != null) await ObserveTaskAsync(active).ConfigureAwait(false);
        if (retry != null) await ObserveTaskAsync(retry).ConfigureAwait(false);
        await CleanupPendingAsync().ConfigureAwait(false);
        var failures = new List<string>();
        if (!_keyboardStopped) await AttemptAsync(async () => { await keyboard.StopAsync().ConfigureAwait(false); _keyboardStopped = true; }, "KEYBOARD_STOP", failures).ConfigureAwait(false);
        if (!_uiaStopped) await AttemptAsync(async () => { await uia.StopAsync().ConfigureAwait(false); _uiaStopped = true; }, "UIA_STOP", failures).ConfigureAwait(false);
        if (failures.Count != 0) throw new IrisException(ErrorCode.CaptureFailed, $"SELECTION_STOP {string.Join(',', failures)}");
        await clipboard.StopAsync().ConfigureAwait(false);
        lock (_gate) _fullyStopped = true;
    }

    private static Task ObserveTaskAsync(Task task) => task.ContinueWith(done => { _ = done.Exception; },
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static async Task AttemptAsync(Func<Task> action, string stage, List<string> failures)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add($"{stage}:0x{ex.HResult:X8}"); }
    }
    private static IrisException Failure(string stage, Exception? first = null, Exception? second = null, ErrorCode code = ErrorCode.CaptureFailed) =>
        new(code, $"SELECTION_{stage} hr=0x{first?.HResult ?? 0:X8} cleanup=0x{second?.HResult ?? 0:X8}");

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); Dispose(); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if ((_used && !_fullyStopped) || _readTask is { IsCompleted: false } || _stopTask is { IsCompleted: false })
                throw new InvalidOperationException("Asynchronous selection shutdown required.");
            _disposed = true;
            _stopped = true;
            if (!_used) _fullyStopped = true;
        }
        _shutdown.Dispose();
    }

    private sealed class Operation(SelectionReadOptions options, CancellationToken caller)
    {
        public SelectionReadOptions Options { get; } = options;
        public CancellationToken Caller { get; } = caller;
        public ForegroundAppInfo? Source { get; set; }
        public CopyFocusPermit? Permit { get; set; }
        public ClipboardSnapshot? Snapshot { get; set; }
        public ClipboardWriteReceipt Clear { get; } = new();
        public ClipboardWriteReceipt Restore { get; } = new();
        public ClipboardVersion? Candidate { get; set; }
        public bool Restored { get; set; }
        public bool TargetChanged { get; set; }
        public Task? CleanupTask { get; set; }
    }
}
