using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Translation;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Platform.Windows.Capture;
using Microsoft.Extensions.Logging;

namespace Iris.App.Capture;

internal interface IScreenshotOverlayFactory
{
    ScreenshotOverlayWindow Create(ScreenshotFrameView frame, int monitor, ScreenshotSettings settings, bool dark);
}
internal sealed class ScreenshotOverlayFactory(IScreenshotSystem system, INativeWindowPlacementAdapter placement) : IScreenshotOverlayFactory
{
    public ScreenshotOverlayWindow Create(ScreenshotFrameView frame, int monitor, ScreenshotSettings settings, bool dark) =>
        new(system, placement, frame, monitor, settings, dark);
}

internal sealed partial class ScreenshotService(
    ISettingsService settings, ScreenshotPreparationCoordinator preparation, IScreenCapturer capturer,
    IClipboardService clipboard, IDesktopContext desktop, IUiDispatcher ui, IScreenshotSystem system,
    ScreenshotRenderSynchronizer render, IScreenshotOverlayFactory factory,
    TranslateWindowPresenter translation, SettingsWindowPresenter settingsWindow, ThemeService theme,
    ILogger<ScreenshotService> logger, IEnumerable<IScreenshotPreparationParticipant>? participants = null) : IScreenshotService
{
    private readonly object _gate = new();
    private Session? _active, _retained;
    private bool _stopping;
    private Task? _stop;
    private readonly ISettingsService _settings = settings;
    private readonly ScreenshotPreparationCoordinator _preparation = preparation;
    private readonly IScreenCapturer _capturer = capturer;
    private readonly IClipboardService _clipboard = clipboard;
    private readonly IDesktopContext _desktop = desktop;
    private readonly IUiDispatcher _ui = ui;
    private readonly IScreenshotSystem _system = system;
    private readonly ScreenshotRenderSynchronizer _render = render;
    private readonly IScreenshotOverlayFactory _factory = factory;
    private readonly TranslateWindowPresenter _translation = translation;
    private readonly SettingsWindowPresenter _settingsWindow = settingsWindow;
    private readonly ThemeService _theme = theme;
    private readonly ILogger<ScreenshotService> _logger = logger;
    private readonly IScreenshotPreparationParticipant[] _participants = participants?.ToArray() ?? [];

    public Task<ScreenshotResult?> CaptureRegionAsync(ScreenshotPurpose purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(purpose)) throw Failure("PURPOSE", code: ErrorCode.InvalidArgument);
        Session session;
        lock (_gate)
        {
            if (_stopping || _active != null || _stop is { IsCompleted: false }) throw Failure("BUSY_OR_STOPPED");
            session = new(this, _retained, cancellationToken); _active = session; _retained = null;
        }
        session.Start();
        return session.Result.Task;
    }
    internal bool HasPendingOverlays { get { lock (_gate) return (_active ?? _retained)?.Windows.Any(w => !w.IsClosed) == true; } }
    internal IReadOnlyList<ScreenshotOverlayWindow> Windows { get { lock (_gate) return (_active ?? _retained)?.Windows.ToArray() ?? []; } }
    internal void StopAcceptance()
    {
        Session? active;
        lock (_gate) { _stopping = true; active = _active; }
        active?.Cancel(EndReason.Stop);
        _preparation.StopAcceptance();
    }
    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_stop is { IsCompleted: false }) return _stop;
            if (_active == null && _retained == null) return Task.CompletedTask;
            return _stop = Task.Run(CleanupRetainedAsync);
        }
    }
    internal Task RetryCleanupAsync()
    {
        lock (_gate)
        {
            if (_stop is { IsCompleted: false }) return _stop;
            if (_active != null) return _active.Done.Task;
            if (_retained == null) return Task.CompletedTask;
            return _stop = Task.Run(CleanupRetainedAsync);
        }
    }
    private async Task CleanupRetainedAsync()
    {
        Session? active;
        lock (_gate) active = _active;
        if (active != null) await active.Done.Task.ConfigureAwait(false);
        Session? retained;
        lock (_gate) retained = _retained;
        if (retained != null)
        {
            await retained.CleanupAsync().ConfigureAwait(false);
            await retained.DetachAsync().ConfigureAwait(false);
            lock (_gate) if (ReferenceEquals(_retained, retained)) _retained = null;
        }
    }
    internal static ScreenshotException Failure(string stage, int hr = 0, ErrorCode code = ErrorCode.CaptureFailed) =>
        new ScreenshotException(code, $"stage={stage};hr=0x{hr:X8}");
    private void Log(string stage, Exception? ex = null)
    {
        try { LogStage(_logger, stage, ex?.HResult ?? 0); }
        catch (Exception) { /* Logging cannot abandon owned windows. */ }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "Screenshot stage={Stage}; hr={HResult}")]
    private static partial void LogStage(ILogger logger, string stage, int hResult);
    private enum EndReason { None, User, Caller, Display, External, Stop }

    private sealed class Session(ScreenshotService owner, Session? previous, CancellationToken caller) : IAsyncDisposable
    {
        internal readonly TaskCompletionSource<ScreenshotResult?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<ScreenshotOverlayWindow> Windows = [];
        private readonly List<WindowPreparationIntent.Receipt> _observations = [];
        private readonly CancellationTokenSource _source = new();
        private readonly TaskCompletionSource<PixelRect?> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ScreenshotSelection _selection = new();
        private readonly HashSet<nint> _handles = [];
        private CancellationTokenRegistration _callerRegistration, _leaseRegistration;
        private Task? _cancelTask;
        private ScreenshotPreparationCoordinator.Lease? _lease;
        private ScreenshotSettings? _settings;
        private ScreenshotFrameView? _frameView;
        private ImageData? _image;
        private EndReason _reason;
        private Exception? _inputFailure, _initialFailure;
        private bool _detached, _preparationPending;
        private bool _interactive, _confirmed, _finished, _sealed, _focusChecked, _focusEligible, _copyWarning;
        private ScreenshotFocus _original;
        private Session? _previous = previous;
        internal void Start()
        {
            try
            {
                // Observation spans A lease return, late focus/notification queues and final settlement.
                _observations.Add(owner._translation.ObservePreparation(() => Cancel(EndReason.Display)));
                _observations.Add(owner._settingsWindow.ObservePreparation(() => Cancel(EndReason.Display)));
                foreach (var participant in owner._participants) _observations.Add(participant.ObservePreparation(() => Cancel(EndReason.Display)));
                _callerRegistration = caller.Register(() => Cancel(EndReason.Caller));
                _settings = owner._settings.Current.Screenshot with { };
            }
            catch (Exception ex) { _initialFailure = ex; }
            _ = Task.Run(RunAsync);
        }
        internal void Cancel(EndReason reason)
        {
            lock (owner._gate)
            {
                if (_finished) return;
                if (_reason == EndReason.None) _reason = reason;
                if (!_sealed) _cancelTask ??= Task.Run(async () => await _source.CancelAsync().ConfigureAwait(false));
                _decision.TrySetResult(null);
            }
        }
        private void Check()
        {
            if (_observations.Any(o => !o.IsCurrent)) Cancel(EndReason.Display);
            if (caller.IsCancellationRequested) Cancel(EndReason.Caller);
            lock (owner._gate) if (_reason != EndReason.None) throw new OperationCanceledException();
        }
        private bool IsOwned(ScreenshotFocus focus) => focus.Process == (uint)Environment.ProcessId && _handles.Contains(focus.Handle);
        private bool SameTopology()
        {
            var current = ScreenPixelLayout.Snapshot(owner._desktop.GetMonitors());
            return _frameView != null && current.Bounds == _frameView.Frame.VirtualBounds &&
                ScreenPixelLayout.SameTopology(current.Monitors, _frameView.Frame.Monitors);
        }
        private async Task<T> CaptureBoundaryAsync<T>(Func<Task<T>> operation)
        {
            try { return await operation().ConfigureAwait(false); }
            catch (IrisException ex) when (owner._capturer is GdiScreenCapturer)
            {
                // T-025 constructs these details from fixed stages and numeric native values.
                throw new ScreenshotException(ex.Code, ex.Detail ?? $"CAPTURE=0x{ex.HResult:X8}");
            }
        }
        private async Task RunAsync()
        {
            Exception? failure = null; PixelRect? region = null; string stage = "START";
            try
            {
                if (_initialFailure != null) throw _initialFailure;
                if (_previous != null)
                {
                    await _previous.CleanupAsync().ConfigureAwait(false);
                    await _previous.DetachAsync().ConfigureAwait(false); _previous = null;
                }
                Check();
                if (!double.IsFinite(_settings!.MaskOpacity) || _settings.MaskOpacity < 0 || _settings.MaskOpacity > .9)
                    throw Failure("SETTINGS", code: ErrorCode.InvalidArgument);
                _original = owner._system.Foreground();
                stage = "PREPARE";
                _preparationPending = true;
                _lease = await owner._preparation.PrepareAsync(_settings.HideIrisWindows, _source.Token).ConfigureAwait(false);
                _leaseRegistration = _lease.Revoked.Register(() => Cancel(EndReason.Display));
                Check(); stage = "RENDER"; await owner._render.SynchronizeAsync().ConfigureAwait(false);
                Check(); stage = "CAPTURE";
                var frame = await CaptureBoundaryAsync(() => owner._capturer.CaptureVirtualScreenAsync(_source.Token)).ConfigureAwait(false);
                Check(); stage = "FRAME";
                _frameView = await Task.Run(() => new ScreenshotFrameView(frame)).ConfigureAwait(false);
                frame = null!;
                Check(); stage = "OVERLAYS";
                await owner._ui.InvokeAsync(() =>
                {
                    for (int i = 0; i < _frameView.Frame.Monitors.Count; i++)
                    {
                        Check();
                        bool dark = owner._theme.EffectiveTheme == AppTheme.Dark || owner._theme.EffectiveTheme == AppTheme.System &&
                            Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
                        var window = owner._factory.Create(_frameView, i, _settings, dark);
                        lock (owner._gate) Windows.Add(window); // Pure managed view registered before HWND initialization.
                        window.Input += OnInput; window.InputFailed += OnInputFailure; window.Closed += OnWindowClosed;
                        window.ShowOwned(Check); Check();
                        if (window.IsClosed) { Cancel(EndReason.External); Check(); }
                        _handles.Add(window.Handle);
                        window.UpdateSelection(null, new(window.Monitor.Bounds.X, window.Monitor.Bounds.Y));
                    }
                    Check();
                    if (!SameTopology()) { Cancel(EndReason.External); return Task.CompletedTask; }
                    _interactive = true;
                    foreach (var window in Windows) { window.Established = true; window.AcceptPointerInput = true; }
                    Check(); owner._system.Activate(Windows[0]);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                region = await _decision.Task.ConfigureAwait(false);
                if (_inputFailure != null) throw _inputFailure;
                Check();
                if (region != null)
                {
                    stage = "CROP";
                    _image = await CaptureBoundaryAsync(() => owner._capturer.CropAsync(_frameView.Frame, region.Value, ImageFormat.Png, _source.Token)).ConfigureAwait(false);
                    Check();
                    if (_image.Format != ImageFormat.Png || _image.PixelWidth != region.Value.Width || _image.PixelHeight != region.Value.Height)
                        throw Failure("CROP_DIMENSIONS", code: ErrorCode.ImageInvalid);
                    if (_settings.CopyImageToClipboard)
                    {
                        stage = "COPY"; Check();
                        try { await owner._clipboard.SetImageAsync(_image, _source.Token).ConfigureAwait(false); }
                        catch (Exception ex) { _copyWarning = true; owner.Log("COPY", ex); }
                        Check();
                    }
                }
            }
            catch (OperationCanceledException) when (_reason != EndReason.None) { }
            catch (Exception ex)
            {
                failure = ScreenshotErrors.Wrap(stage, ex);
            }
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                failure = new ScreenshotException(ErrorCode.CaptureFailed,
                    $"primary=[{(failure == null ? "none" : ScreenshotErrors.Describe("PRIMARY", failure))}];{ScreenshotErrors.Describe("CLEANUP", ex)}");
            }
            bool pending = _preparationPending || Windows.Count != 0 || _previous != null;
            if (!pending)
            {
                if (failure == null)
                {
                    try { await FinishUiAsync().ConfigureAwait(false); }
                    catch (Exception ex) { owner.Log("FINAL_UI", ex); }
                }
            }
            try { await DetachAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure = new ScreenshotException(ErrorCode.CaptureFailed, $"primary=[{(failure as IrisException)?.Detail}];cancel=[{ScreenshotErrors.Describe("CANCEL", ex)}]"); }
            lock (owner._gate)
            {
                if (_observations.Any(o => !o.IsCurrent) && _reason == EndReason.None) _reason = EndReason.Display;
                if (caller.IsCancellationRequested && _reason == EndReason.None) _reason = EndReason.Caller;
                _finished = true;
                foreach (var observation in _observations) observation.Dispose(); _observations.Clear();
                if (pending) owner._retained = _previous ?? this;
                owner._active = null;
                if (failure != null) Result.TrySetException(failure);
                else if (_reason == EndReason.Caller) Result.TrySetCanceled(caller);
                else Result.TrySetResult(_reason == EndReason.None && region != null && _image != null ? new(_image, region.Value) : null);
                _image = null;
                Done.TrySetResult();
            }
        }
        private void OnWindowClosed(object? sender, EventArgs args)
        {
            if (_interactive && !_confirmed) Cancel(EndReason.External);
        }
        private void OnInputFailure(Exception ex)
        {
            _inputFailure = ScreenshotErrors.Wrap("INPUT", ex); Cancel(EndReason.External);
        }
        private void OnInput(object? sender, ScreenshotInputEventArgs args)
        {
            if (!_interactive || _confirmed || sender is not ScreenshotOverlayWindow window) return;
            try
            {
                Check();
                var point = ScreenshotSelection.Clamp(args.Point, window.Monitor.Bounds);
                switch (args.Input)
                {
                    case ScreenshotInput.Topology: Cancel(EndReason.External); break;
                    case ScreenshotInput.Deactivated:
                        if (!IsOwned(owner._system.Foreground())) Cancel(EndReason.External);
                        break;
                    case ScreenshotInput.LostCapture: Cancel(EndReason.External); break;
                    case ScreenshotInput.Escape: Cancel(EndReason.User); break;
                    case ScreenshotInput.Right:
                        if (_selection.Region != null) { _selection.Clear(); foreach (var w in Windows) w.ReleasePointer(); }
                        else Cancel(EndReason.User);
                        break;
                    case ScreenshotInput.DoubleClick:
                        if (!_settings!.ConfirmOnMouseUp && _selection.Monitor == window.MonitorIndex && _selection.Region is { } r && ScreenshotSelection.Contains(r, point)) Confirm();
                        break;
                    case ScreenshotInput.Down:
                        foreach (var w in Windows) w.ReleasePointer();
                        _selection.Down(window.MonitorIndex, window.Monitor.Bounds, point, !_settings!.ConfirmOnMouseUp); window.CapturePointer(); break;
                    case ScreenshotInput.Move:
                        if (_selection.Dragging) _selection.Move(Windows[_selection.Monitor].Monitor.Bounds, args.Point);
                        break;
                    case ScreenshotInput.Up:
                        if (_selection.Dragging)
                        {
                            _selection.Up(Windows[_selection.Monitor].Monitor.Bounds, args.Point);
                            foreach (var w in Windows) w.ReleasePointer();
                            if (_settings!.ConfirmOnMouseUp) Confirm();
                        }
                        break;
                    case ScreenshotInput.Enter: if (!_selection.Dragging) Confirm(); break;
                }
                foreach (var w in Windows) w.UpdateSelection(_selection.Monitor == w.MonitorIndex ? _selection.Region : null,
                    args.Input is ScreenshotInput.Down or ScreenshotInput.Move or ScreenshotInput.Up or ScreenshotInput.DoubleClick or ScreenshotInput.Right
                        ? ScreenshotSelection.Clamp(args.Point, w.Monitor.Bounds) : null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnInputFailure(ex); }
        }
        private void Confirm()
        {
            if (!ScreenshotSelection.Valid(_selection.Region) || _confirmed) return;
            Check();
            if (!SameTopology()) { Cancel(EndReason.External); return; }
            var r = _selection.Region!.Value; var b = Windows[_selection.Monitor].Monitor.Bounds;
            if (r.X < b.X || r.Y < b.Y || (long)r.X + r.Width > (long)b.X + b.Width || (long)r.Y + r.Height > (long)b.Y + b.Height)
                throw Failure("REGION");
            _confirmed = true; _interactive = false;
            foreach (var window in Windows) window.AcceptPointerInput = false;
            foreach (var window in Windows) window.ReleasePointer();
            _decision.TrySetResult(r);
        }
        internal async Task CleanupAsync()
        {
            var errors = new List<string>();
            await owner._ui.InvokeAsync(() =>
            {
                _interactive = false;
                foreach (var window in Windows) window.AcceptPointerInput = false;
                if (!_focusChecked)
                {
                    _focusChecked = true;
                    try { _focusEligible = (_confirmed || _reason == EndReason.User) && IsOwned(owner._system.Foreground()); }
                    catch (Exception ex) { owner.Log("FOCUS_SNAPSHOT", ex); }
                }
                foreach (var window in Windows.ToArray())
                {
                    try { window.CloseOwned(); }
                    catch (Exception ex) { errors.Add(ScreenshotErrors.Describe("CLOSE", ex)); }
                    if (window.IsClosed)
                    {
                        window.Input -= OnInput; window.InputFailed -= OnInputFailure; window.Closed -= OnWindowClosed;
                        lock (owner._gate) Windows.Remove(window);
                    }
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            if (Windows.Count == 0)
            {
                _frameView = null;
                if (_lease != null)
                {
                    try { await _lease.DisposeAsync().ConfigureAwait(false); _lease = null; _preparationPending = false; }
                    catch (Exception ex) { errors.Add(ScreenshotErrors.Describe("PREPARATION", ex)); }
                }
                else if (_preparationPending)
                {
                    try { await owner._preparation.RetryCleanupAsync().ConfigureAwait(false); _preparationPending = false; }
                    catch (Exception ex) { errors.Add(ScreenshotErrors.Describe("PREPARATION", ex)); }
                }
            }
            if (errors.Count != 0) throw new ScreenshotException(ErrorCode.CaptureFailed, string.Join(';', errors));
        }
        private Task FinishUiAsync() => owner._ui.InvokeAsync(() =>
        {
            bool Current()
            {
                if (_observations.Any(o => !o.IsCurrent)) Cancel(EndReason.Display);
                if (caller.IsCancellationRequested) Cancel(EndReason.Caller);
                lock (owner._gate) return !owner._stopping && _reason is EndReason.None or EndReason.User;
            }
            bool MayRestore()
            {
                if (!Current() || !_focusEligible || !owner._system.IsTarget(_original)) return false;
                var now = owner._system.Foreground();
                return now != _original && (now.Handle == 0 || IsOwned(now)) &&
                    owner._system.IsTarget(_original) && Current();
            }
            try
            {
                if (MayRestore() && !owner._system.RestoreForeground(_original.Handle, MayRestore)) owner.Log("FOCUS_RESTORE");
            }
            catch (Exception ex) { owner.Log("FOCUS_RESTORE", ex); }
            bool MayWarn() => Current() && _reason == EndReason.None && _image != null;
            if (_copyWarning && MayWarn())
            {
                try { owner._system.WarnCopyFailure(MayWarn); } catch (Exception ex) { owner.Log("COPY_WARNING", ex); }
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
        internal async Task DetachAsync()
        {
            if (_detached) return;
            await _leaseRegistration.DisposeAsync().ConfigureAwait(false);
            await _callerRegistration.DisposeAsync().ConfigureAwait(false);
            Task? cancel;
            lock (owner._gate) { _sealed = true; cancel = _cancelTask; }
            try { if (cancel != null) await cancel.ConfigureAwait(false); }
            finally { _source.Dispose(); _detached = true; }
        }
        public async ValueTask DisposeAsync() { await CleanupAsync().ConfigureAwait(false); await DetachAsync().ConfigureAwait(false); }
    }
}
