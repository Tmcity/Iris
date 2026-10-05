using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Platform.Windows.Selection;
using Microsoft.Extensions.Logging;

namespace Iris.App.Selection;

internal sealed record MouseSelectionRuntimeSnapshot(long Version, MouseSelectionMode DesiredMode,
    MouseSelectionMode ActualMode, bool IsApplying, ErrorCode? Error, bool CanRetry);

/// <summary>有界配置、手势及 UI 调度；平台事务仍由 A/B1/C2 拥有。</summary>
internal sealed partial class SelectionIconController(
    ISettingsService settings, IMouseSelectionMonitor monitor, SelectionReadCoordinator reader,
    IActionDispatcher dispatcher, IDesktopContext desktop, IUiDispatcher ui, ISelectionIconViewFactory views,
    SelectionIconNative native, TrayNotificationService notifications, TimeProvider time,
    ILogger<SelectionIconController> logger) : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private SelectionSettings? _desired, _applied;
    private bool _started, _stopping, _fullyStopped, _disposed;
    private bool _applyRequested, _applying, _workRunning, _visualRunning, _monitorStopped = true, _forceRestart;
    private long _configuration, _request, _visualVersion;
    private Task _applyTask = Task.CompletedTask, _workTask = Task.CompletedTask, _visualTask = Task.CompletedTask, _timerCleanup = Task.CompletedTask;
    private Task? _stopTask;
    private WorkInput? _pending;
    private Work? _active;
    private Candidate? _candidate;
    private ITimer? _timer;
    private ISelectionIconView? _view; // Only UI dispatcher accesses the view itself.
    private long _visibleCandidate;
    private IrisException? _visualFailure;
    private MouseSelectionRuntimeSnapshot _runtime = new(0, MouseSelectionMode.Off, MouseSelectionMode.Off, false, null, false);
    internal event EventHandler<MouseSelectionRuntimeSnapshot>? RuntimeChanged;
    internal MouseSelectionRuntimeSnapshot Runtime { get { lock (_gate) return _runtime; } }

    internal Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping) return Task.CompletedTask;
            if (!_started)
            {
                _started = true;
                settings.Changed += OnSettingsChanged;
                monitor.SelectionGestureDetected += OnGesture;
                monitor.MouseDown += OnMouseDown;
                monitor.Invalidated += OnInvalidated;
            }
        }
        return ApplyLatestAsync(force: false);
    }
    internal Task RetryAsync() => ApplyLatestAsync(force: true);
    internal Task WaitForApplyAsync() { lock (_gate) return _applyTask; }
    internal async Task WaitForWorkAsync()
    {
        Task work;
        lock (_gate) work = _workTask;
        await work.ConfigureAwait(false);
        Task visual;
        lock (_gate) visual = _visualTask;
        await visual.ConfigureAwait(false);
    }

    private Task ApplyLatestAsync(bool force)
    {
        var snapshot = Freeze(settings.Current.Selection);
        lock (_gate)
        {
            if (_stopping || !_started) return Task.CompletedTask;
            if (!force && SameSettings(snapshot, _desired)) return _applyTask;
            _desired = snapshot; _configuration++; _applyRequested = true; _forceRestart |= force;
            RevokeLocked();
            SetRuntimeLocked(true, null, false);
            if (!_applying) { _applying = true; _applyTask = Task.Run(ApplyLoopAsync); }
            return _applyTask;
        }
    }
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        try { _ = ApplyLatestAsync(false); }
        catch (Exception ex) { LogFailure("Settings", ex); }
    }

    private async Task ApplyLoopAsync()
    {
        while (true)
        {
            SelectionSettings target;
            long version;
            bool restart;
            Task work;
            lock (_gate)
            {
                if (!_applyRequested || _stopping) { _applying = false; return; }
                _applyRequested = false; target = _desired!; version = _configuration;
                restart = _forceRestart || !SameMonitor(target, _applied); _forceRestart = false;
                work = _workTask;
            }
            ErrorCode? failure = null;
            try
            {
                await work.ConfigureAwait(false);
                if (restart && !_monitorStopped) { await monitor.StopAsync().ConfigureAwait(false); _monitorStopped = true; }
                lock (_gate) { if (_stopping || version != _configuration) continue; }
                if (restart && target.MouseMode != MouseSelectionMode.Off)
                {
                    _monitorStopped = false; // Includes an installation that has not returned yet.
                    await monitor.StartAsync(new(target.MinDragDistancePx, target.RequireIBeamCursor, target.ExcludedProcesses)).ConfigureAwait(false);
                }
                Task visual;
                lock (_gate) { QueueVisualLocked(); visual = _visualTask; }
                await visual.ConfigureAwait(false);
                lock (_gate)
                {
                    if (_visualFailure != null) throw _visualFailure;
                    if (!_stopping && version == _configuration) _applied = target;
                }
            }
            catch (Exception ex)
            {
                LogFailure("Apply", ex); failure = ErrorCode.HookInstallFailed;
                lock (_gate) _forceRestart = true;
            }
            lock (_gate)
            {
                if (!_stopping && version == _configuration) SetRuntimeLocked(false, failure, failure != null);
            }
            if (failure != null)
            {
                try { await ui.InvokeAsync(() =>
                {
                    bool current;
                    lock (_gate) current = !_stopping && version == _configuration && _runtime.Error == ErrorCode.HookInstallFailed;
                    if (current) notifications.TryNotifyNow(NotificationLevel.Warning, ErrorCode.HookInstallFailed);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { LogFailure("Notify", ex); }
            }
        }
    }

    private void OnGesture(object? sender, SelectionGestureEventArgs args)
    {
        lock (_gate)
        {
            if (!CanAcceptLocked(args.Generation) || !ValidSource(args.App, _desired!)) return;
            RevokeLocked();
            _pending = new(args.CursorPosition, args.App! with { WindowTitle = null }, args.Generation, _configuration, _request, _desired!, null);
            EnsureWorkLocked();
        }
    }
    private void OnMouseDown(object? sender, MouseSelectionInputEventArgs args)
    {
        lock (_gate)
        {
            if (_stopping || args.Generation != monitor.Generation) return;
            if (_candidate is { } candidate && _visibleCandidate == candidate.Input.Request && candidate.Bounds.Contains(args.CursorPosition)) return;
            RevokeLocked();
        }
    }
    private void OnInvalidated(object? sender, MouseSelectionInvalidatedEventArgs args)
    {
        lock (_gate)
        {
            if (_stopping || args.Generation != monitor.Generation) return;
            RevokeLocked();
            if (args.Error != null) { _forceRestart = true; SetRuntimeLocked(false, ErrorCode.HookInstallFailed, true); }
        }
    }
    private bool CanAcceptLocked(long generation) => _started && !_stopping && !_applying && !_applyRequested &&
        _desired?.MouseMode != MouseSelectionMode.Off && _applied != null && monitor.IsRunning && generation == monitor.Generation;
    private bool CurrentLocked(WorkInput input) => !_stopping && input.Request == _request && input.Configuration == _configuration &&
        input.Generation == monitor.Generation && _desired?.MouseMode == input.Settings.MouseMode && monitor.IsRunning;

    private void RevokeLocked()
    {
        _request++; _pending = null;
        CancelWorkLocked();
        ClearCandidateLocked();
    }
    private void CancelWorkLocked()
    {
        if (_active is not { Sealed: false } active || active.Dispatch is { IsCompleted: true } || active.Cancellation != null) return;
        active.Cancellation = Task.Run(async () =>
        {
            bool starting;
            lock (_gate) starting = active.DispatchStarting;
            if (starting) await active.DispatchReady.Task.ConfigureAwait(false);
            if (active.Dispatch is not { IsCompleted: true }) await active.Source.CancelAsync().ConfigureAwait(false);
        });
    }
    private void ClearCandidateLocked()
    {
        _candidate = null; _visibleCandidate = 0;
        if (_timer is { } timer)
        {
            _timer = null;
            _timerCleanup = Task.Run(async () => await timer.DisposeAsync().ConfigureAwait(false));
        }
        QueueVisualLocked();
    }
    private void EnsureWorkLocked()
    {
        if (_workRunning) return;
        _workRunning = true;
        _workTask = Task.Run(WorkLoopAsync);
    }
    private async Task WorkLoopAsync()
    {
        while (true)
        {
            Work work;
            lock (_gate)
            {
                if (_pending == null || _stopping || _applying) { _workRunning = false; return; }
                work = new(_pending); _pending = null; _active = work;
            }
            try { await ProcessAsync(work).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogFailure("ReadDispatch", ex);
                lock (_gate) if (CurrentLocked(work.Input)) SetRuntimeLocked(false, ErrorCode.CaptureFailed, false);
            }
            finally
            {
                Task? cancellation;
                lock (_gate) { work.Sealed = true; cancellation = work.Cancellation; }
                if (cancellation != null)
                {
                    try { await cancellation.ConfigureAwait(false); }
                    catch (Exception ex) { LogFailure("Cancel", ex); }
                }
                work.Dispose();
                lock (_gate) _active = null;
            }
        }
    }
    private async Task ProcessAsync(Work work)
    {
        var input = work.Input;
        if (input.Text == null) await Task.Delay(TimeSpan.FromMilliseconds(120), time, work.Source.Token).ConfigureAwait(false);
        lock (_gate) if (!CurrentLocked(input)) return;
        if (!SameSource(desktop.GetForegroundApp(), input.App)) return;
        if (input.Text != null || input.Settings.MouseMode == MouseSelectionMode.AutoTranslate)
        {
            var context = new ActionContext(input.Text != null ? ActionTrigger.SelectionIcon : ActionTrigger.MouseSelection, input.Anchor, input.Text)
            { ExpectedSelectionSource = input.App };
            lock (_gate) work.DispatchStarting = true;
            try { work.Dispatch = dispatcher.DispatchAsync(IrisAction.SelectionTranslate, context, work.Source.Token); }
            finally { work.DispatchReady.TrySetResult(); }
            await work.Dispatch.ConfigureAwait(false);
            return;
        }
        var result = await reader.ReadPassiveAsync(new(input.Settings.ReadOrder, input.Settings.RestoreClipboard,
            input.Settings.ClipboardTimeoutMs, input.Settings.ClipboardUnsafeProcesses) { ExpectedSource = input.App }, work.Source.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result?.Text) || result.Text.Length > 65_536 || !SameSource(result.SourceApp, input.App)) return;
        if (!SameSource(desktop.GetForegroundApp(), input.App)) return;
        var bounds = SelectionIconGeometry.Place(input.Anchor, desktop.GetMonitorAt(input.Anchor));
        Task timerCleanup;
        lock (_gate) timerCleanup = _timerCleanup;
        await timerCleanup.ConfigureAwait(false);
        lock (_gate)
        {
            if (!CurrentLocked(input)) return;
            _candidate = new(input with { Text = result.Text }, bounds);
            _timer = time.CreateTimer(_ =>
            {
                lock (_gate) if (_candidate?.Input.Request == input.Request) { _request++; ClearCandidateLocked(); }
            }, null, TimeSpan.FromMilliseconds(input.Settings.IconAutoHideMs), Timeout.InfiniteTimeSpan);
            QueueVisualLocked();
        }
    }

    private void OnClick(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_candidate is not { } candidate || _visibleCandidate != candidate.Input.Request || !CurrentLocked(candidate.Input)) return;
            // This callback runs on our UI dispatcher. Only bounded HWND/PID metadata is queried here.
            if (!native.IsForeground(candidate.Input.App)) { RevokeLocked(); return; }
            ClearCandidateLocked();
            _pending = candidate.Input with { Anchor = new(candidate.Bounds.X, candidate.Bounds.Y) };
            EnsureWorkLocked();
        }
    }

    private void QueueVisualLocked()
    {
        _visualVersion++;
        if (_visualRunning) return;
        _visualRunning = true; _visualTask = Task.Run(VisualLoopAsync);
    }
    private async Task VisualLoopAsync()
    {
        while (true)
        {
            long version;
            lock (_gate) version = _visualVersion;
            try
            {
                await ui.InvokeAsync(() =>
                {
                    Candidate? candidate; bool close;
                    lock (_gate)
                    {
                        candidate = _candidate is { } c && CurrentLocked(c.Input) ? c : null;
                        close = _stopping || _desired?.MouseMode == MouseSelectionMode.Off;
                    }
                    if (close)
                    {
                        if (_view != null) { _view.CloseIcon(); _view.Clicked -= OnClick; _view = null; }
                    }
                    else if (candidate != null && native.IsForeground(candidate.Input.App))
                    {
                        if (_view == null) { _view = views.Create(); _view.Clicked += OnClick; }
                        lock (_gate) { if (!CurrentLocked(candidate.Input) || _candidate != candidate) return Task.CompletedTask; }
                        _view.ShowAt(candidate.Bounds);
                        lock (_gate) if (_candidate == candidate) _visibleCandidate = candidate.Input.Request;
                    }
                    else
                    {
                        if (candidate != null)
                        {
                            lock (_gate) if (_candidate == candidate) { _request++; ClearCandidateLocked(); }
                        }
                        _view?.HideIcon();
                    }
                    lock (_gate) _visualFailure = null;
                    PublishRuntime();
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure("Icon", ex);
                lock (_gate)
                {
                    _visualFailure = Failure("ICON", ex); _candidate = null; _visibleCandidate = 0;
                    _runtime = _runtime with { Version = _runtime.Version + 1, Error = ErrorCode.HookInstallFailed, CanRetry = true };
                }
                try { await ui.InvokeAsync(() => { PublishRuntime(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception publishError) { LogFailure("RuntimeUi", publishError); }
            }
            lock (_gate) if (version == _visualVersion) { _visualRunning = false; return; }
        }
    }
    private void SetRuntimeLocked(bool applying, ErrorCode? error, bool retry)
    {
        _runtime = new(_runtime.Version + 1, _desired?.MouseMode ?? MouseSelectionMode.Off,
            monitor.IsRunning ? _applied?.MouseMode ?? _desired?.MouseMode ?? MouseSelectionMode.Off : MouseSelectionMode.Off, applying, error, retry);
        QueueVisualLocked();
    }
    private void PublishRuntime()
    {
        MouseSelectionRuntimeSnapshot snapshot;
        lock (_gate) snapshot = _runtime;
        if (RuntimeChanged is not { } handlers) return;
        foreach (EventHandler<MouseSelectionRuntimeSnapshot> handler in handlers.GetInvocationList())
            try { handler(this, snapshot); } catch (Exception ex) { LogFailure("RuntimeSubscriber", ex); }
    }

    internal void StopAcceptance()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true; _applyRequested = false; RevokeLocked();
            settings.Changed -= OnSettingsChanged;
            monitor.SelectionGestureDetected -= OnGesture; monitor.MouseDown -= OnMouseDown; monitor.Invalidated -= OnInvalidated;
        }
    }
    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_fullyStopped) return Task.CompletedTask;
            return _stopTask = Task.Run(StopCoreAsync);
        }
    }
    private async Task StopCoreAsync()
    {
        var errors = new List<string>();
        try { await monitor.StopAsync().ConfigureAwait(false); _monitorStopped = true; } catch (Exception ex) { errors.Add($"MONITOR:0x{ex.HResult:X8}"); }
        try { await _applyTask.ConfigureAwait(false); } catch (Exception ex) { errors.Add($"APPLY:0x{ex.HResult:X8}"); }
        // An accepted configuration may have crossed its final check when Stop first reached A.
        // Drain the application, then close any late monitor ownership as well.
        try { await monitor.StopAsync().ConfigureAwait(false); _monitorStopped = true; } catch (Exception ex) { errors.Add($"LATE_MONITOR:0x{ex.HResult:X8}"); }
        try { await _workTask.ConfigureAwait(false); } catch (Exception ex) { errors.Add($"WORK:0x{ex.HResult:X8}"); }
        try { await _timerCleanup.ConfigureAwait(false); } catch (Exception ex) { errors.Add($"TIMER:0x{ex.HResult:X8}"); }
        Task visual;
        lock (_gate) { QueueVisualLocked(); visual = _visualTask; }
        await visual.ConfigureAwait(false);
        lock (_gate)
        {
            if (_visualFailure != null) errors.Add($"WINDOW:0x{_visualFailure.HResult:X8}");
            if (errors.Count == 0) _fullyStopped = true;
        }
        if (errors.Count != 0) throw new IrisException(ErrorCode.HookInstallFailed, ErrorMessages.GetDefault(ErrorCode.HookInstallFailed), detail: $"phase=STOP;{string.Join(';', errors)}");
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); Dispose(); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_started && !_fullyStopped) throw new InvalidOperationException("Asynchronous mouse shutdown required.");
            if (!_started) _fullyStopped = true;
            _disposed = _stopping = true;
        }
    }
    private static SelectionSettings Freeze(SelectionSettings value) => value with
    { ReadOrder = value.ReadOrder.ToArray(), ClipboardUnsafeProcesses = value.ClipboardUnsafeProcesses.ToArray(), ExcludedProcesses = value.ExcludedProcesses.ToArray() };
    private static bool SameSettings(SelectionSettings value, SelectionSettings? other) => SameMonitor(value, other) && other != null &&
        value.IconAutoHideMs == other.IconAutoHideMs && value.RestoreClipboard == other.RestoreClipboard && value.ClipboardTimeoutMs == other.ClipboardTimeoutMs &&
        value.ReadOrder.SequenceEqual(other.ReadOrder) && value.ClipboardUnsafeProcesses.SequenceEqual(other.ClipboardUnsafeProcesses, StringComparer.OrdinalIgnoreCase);
    private static bool SameMonitor(SelectionSettings value, SelectionSettings? other) => other != null && value.MouseMode == other.MouseMode &&
        value.RequireIBeamCursor == other.RequireIBeamCursor && value.MinDragDistancePx == other.MinDragDistancePx && value.ExcludedProcesses.SequenceEqual(other.ExcludedProcesses, StringComparer.OrdinalIgnoreCase);
    private static bool SameSource(ForegroundAppInfo? actual, ForegroundAppInfo expected) => actual != null && SelectionReadOptionsSnapshot.Matches(actual, expected);
    private static bool ValidSource(ForegroundAppInfo? app, SelectionSettings config) => app is { WindowHandle: not 0, ProcessId: > 0 } &&
        app.ProcessId != Environment.ProcessId && !string.IsNullOrWhiteSpace(app.ProcessName) && app.ProcessName.IndexOfAny(['\\', '/', ':', '\0']) < 0 &&
        !config.ExcludedProcesses.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase);
    private static IrisException Failure(string stage, Exception ex) => new(ErrorCode.HookInstallFailed, ErrorMessages.GetDefault(ErrorCode.HookInstallFailed), detail: $"phase={stage};hr=0x{ex.HResult:X8}");
    private void LogFailure(string stage, Exception ex)
    {
        try { LogSafe(logger, stage, ex.GetType().Name); }
        catch (Exception) { /* A logging sink cannot abandon request/window ownership. */ }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Mouse interaction stage={Stage}; type={Type}")]
    private static partial void LogSafe(ILogger logger, string stage, string type);
    private sealed record WorkInput(PixelPoint Anchor, ForegroundAppInfo App, long Generation, long Configuration, long Request, SelectionSettings Settings, string? Text)
    { public override string ToString() => "MouseSelectionWork"; }
    private sealed record Candidate(WorkInput Input, PixelRect Bounds)
    { public override string ToString() => "MouseSelectionCandidate"; }
    private sealed class Work(WorkInput input) : IDisposable
    {
        internal readonly WorkInput Input = input;
        internal readonly CancellationTokenSource Source = new();
        internal Task? Cancellation;
        internal Task? Dispatch;
        internal bool DispatchStarting;
        internal readonly TaskCompletionSource DispatchReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Sealed;
        public void Dispose() => Source.Dispose();
    }
}
