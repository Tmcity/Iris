using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.App.Translation;

/// <summary>
/// 翻译窗口展示器（App 单例实现）。
/// 管理操作租约生命周期、准入控制、工作槽位调度、窗口按需创建与定位、以及正常退出收尾屏障。
/// 规范与铁律见 TRANSLATION_WINDOW.md 全文与 CCR-013。
/// </summary>
internal sealed partial class TranslateWindowPresenter : ITranslateWindowPresenter, ITranslateWindowShutdownCoordinator
{
    public const int MaxActiveOperations = 8;

    private readonly IServiceProvider _serviceProvider;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ISettingsService _settingsService;
    private readonly IDesktopContext _desktopContext;
    private readonly ILogger<TranslateWindowPresenter> _logger;
    private readonly INativeWindowPlacementAdapter _nativePlacementAdapter;

    private readonly Lock _gate = new();
    private readonly List<TranslateWindowOperation> _activeOperations = [];
    private TranslateWindowOperation? _latestOperation;
    private bool _isShuttingDown;
    private Task? _shutdownTask;

    private bool _isVisibleSnapshot;
    private bool _isActiveSnapshot;

    private IServiceScope? _scope;
    private TranslateWindow? _window;
    private TranslateWindowViewModel? _viewModel;

    private PixelRect? _pendingPlacementSave;
    private Task? _placementSaveTask;
    private int _deactivationSuppressionCount;

    public TranslateWindowPresenter(
        IServiceProvider serviceProvider,
        IUiDispatcher uiDispatcher,
        ISettingsService settingsService,
        IDesktopContext desktopContext,
        ILogger<TranslateWindowPresenter> logger,
        INativeWindowPlacementAdapter? nativePlacementAdapter = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _desktopContext = desktopContext ?? throw new ArgumentNullException(nameof(desktopContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nativePlacementAdapter = nativePlacementAdapter ?? NativeWindowPlacementAdapter.Instance;
    }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _isVisibleSnapshot;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _isActiveSnapshot;
            }
        }
    }

    internal int ActiveOperationsCount
    {
        get
        {
            lock (_gate)
            {
                return _activeOperations.Count;
            }
        }
    }

    internal TranslateWindow? Window => _window;
    internal TranslateWindowViewModel? ViewModel => _viewModel;

    public ITranslateWindowOperation? TryBeginOperation(CancellationToken cancellationToken = default)
    {
        // 已取消令牌抛 OperationCanceledException 且不影响当前意图
        cancellationToken.ThrowIfCancellationRequested();

        TranslateWindowOperation op;
        TranslateWindowOperation? oldOp;
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                return null;
            }

            if (_activeOperations.Count >= MaxActiveOperations)
            {
                return null;
            }

            op = new TranslateWindowOperation(this, cancellationToken);
            _activeOperations.Add(op);
            oldOp = _latestOperation;
            _latestOperation = op;
        }

        // 接纳新操作后，旧操作立即失效并请求取消（在锁外调用）
        oldOp?.Cancel();
        return op;
    }

    public async Task<bool> TryShowAsync(ITranslateWindowOperation operation, TranslateWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);

        if (operation is not TranslateWindowOperation op || !ReferenceEquals(op.Presenter, this))
        {
            throw new ArgumentException("Operation does not belong to this presenter or is invalid.", nameof(operation));
        }

        if (!op.BeginTryShow())
        {
            return false;
        }

        bool accepted = false;
        bool appliedToVm = false;
        try
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (_isShuttingDown || op.IsCallerDisposed || op.CancellationToken.IsCancellationRequested || !ReferenceEquals(_latestOperation, op))
                    {
                        accepted = false;
                        return Task.CompletedTask;
                    }
                }

                EnsureWindowCreated();

                ApplyPlacement(request.Anchor, request.FocusInput);

                _window!.Show();
                if (request.FocusInput)
                {
                    _window.Activate();
                    _window.FocusInputArea();
                }

                lock (_gate)
                {
                    _isVisibleSnapshot = true;
                }

                _viewModel!.ApplyRequest(request, op);
                appliedToVm = true;
                accepted = true;
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);

            return accepted;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _isVisibleSnapshot = false;
            }
            if (appliedToVm)
            {
                _viewModel?.RevokeOperation(op.Id);
            }
            op.Cancel();
            LogShowFailed(_logger, ex.GetType().Name);
            return false;
        }
        finally
        {
            op.EndTryShow(transferredWorkOwnership: accepted);
        }
    }

    public async Task HideAsync()
    {
        TranslateWindowOperation? opToCancel = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_window != null && _window.IsVisible)
            {
                _window.Hide();
            }

            lock (_gate)
            {
                _isVisibleSnapshot = false;
                _isActiveSnapshot = false;
                opToCancel = _latestOperation;
                _latestOperation = null;
            }

            _viewModel?.OnWindowHidden();

            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);

        opToCancel?.Cancel();
    }

    private void EnsureWindowCreated()
    {
        if (_window != null)
        {
            return;
        }

        IServiceScope? scope = null;
        TranslateWindow? window = null;
        TranslateWindowViewModel? vm = null;

        try
        {
            scope = _serviceProvider.CreateScope();
            window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();

            window.DataContext = vm;
            window.AttachPresenter(this);
            vm.AttachPresenter(this);

            _scope = scope;
            _window = window;
            _viewModel = vm;
        }
        catch
        {
            if (window != null)
            {
                try
                {
                    window.ForceClose = true;
                    window.Close();
                }
                catch
                {
                }
            }
            vm?.Dispose();
            scope?.Dispose();
            _scope = null;
            _window = null;
            _viewModel = null;
            throw;
        }
    }

    private void ApplyPlacement(PixelPoint? anchor, bool focusInput)
    {
        if (_window == null)
        {
            return;
        }

        var settings = _settingsService.Current.TranslateWindow;
        var mode = settings.Placement;
        var monitors = _desktopContext.GetMonitors();
        var cursor = _desktopContext.GetCursorPosition();

        var targetMonitor = WindowPlacementHelper.FindTargetMonitor(mode, anchor, settings.LastBounds, monitors, cursor);
        if (targetMonitor == null)
        {
            return;
        }

        int desiredWidthPx = (int)Math.Round(settings.Width * targetMonitor.DpiScale);
        double dipHeight = _window.ActualHeight > 0 ? _window.ActualHeight : 480;
        int desiredHeightPx = (int)Math.Round(dipHeight * targetMonitor.DpiScale);

        var bounds = WindowPlacementHelper.CalculateBounds(
            mode,
            anchor,
            settings.LastBounds,
            targetMonitor,
            desiredWidthPx,
            desiredHeightPx,
            cursor);

        var helper = new WindowInteropHelper(_window);
        if (helper.Handle != IntPtr.Zero)
        {
            _window.BeginPlacementUpdate();
            try
            {
                if (!_nativePlacementAdapter.SetWindowBounds(helper.Handle, bounds, show: true, activate: focusInput))
                {
                    throw new InvalidOperationException("设置窗口物理位置失败。");
                }
            }
            finally
            {
                _window.EndPlacementUpdate();
            }
        }
        else
        {
            void OnSourceInitialized(object? s, EventArgs e)
            {
                _window.SourceInitialized -= OnSourceInitialized;
                var h = new WindowInteropHelper(_window).Handle;
                if (h != IntPtr.Zero)
                {
                    _window.BeginPlacementUpdate();
                    try
                    {
                        if (!_nativePlacementAdapter.SetWindowBounds(h, bounds, show: true, activate: focusInput))
                        {
                            throw new InvalidOperationException("设置窗口物理位置失败。");
                        }
                    }
                    finally
                    {
                        _window.EndPlacementUpdate();
                    }
                }
            }

            _window.SourceInitialized += OnSourceInitialized;
        }
    }

    internal void ReconstrainWindowPlacement()
    {
        if (_window == null || !_window.IsVisible)
        {
            return;
        }

        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
        {
            return;
        }

        if (!_nativePlacementAdapter.TryGetWindowBounds(helper.Handle, out var currentBounds))
        {
            return;
        }

        var monitors = _desktopContext.GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var targetMonitor = WindowPlacementHelper.FindTargetMonitor(
            _settingsService.Current.TranslateWindow.Placement,
            null,
            currentBounds,
            monitors,
            _desktopContext.GetCursorPosition());

        if (targetMonitor == null)
        {
            return;
        }

        var workArea = targetMonitor.WorkArea;
        int clampedWidth = Math.Max(1, Math.Min(currentBounds.Width, workArea.Width));
        int clampedHeight = Math.Max(1, Math.Min(currentBounds.Height, workArea.Height));

        long workAreaLeft = workArea.X;
        long workAreaTop = workArea.Y;
        long workAreaRight = (long)workArea.X + workArea.Width;
        long workAreaBottom = (long)workArea.Y + workArea.Height;

        long clampedX = Math.Clamp((long)currentBounds.X, workAreaLeft, Math.Max(workAreaLeft, workAreaRight - clampedWidth));
        long clampedY = Math.Clamp((long)currentBounds.Y, workAreaTop, Math.Max(workAreaTop, workAreaBottom - clampedHeight));

        var newBounds = new PixelRect((int)clampedX, (int)clampedY, clampedWidth, clampedHeight);
        if (newBounds != currentBounds)
        {
            _window.BeginPlacementUpdate();
            try
            {
                _nativePlacementAdapter.SetWindowBounds(helper.Handle, newBounds, show: true, activate: false);
            }
            finally
            {
                _window.EndPlacementUpdate();
            }
        }
    }

    internal void OnUserChangedPlacement(PixelRect bounds)
    {
        lock (_gate)
        {
            _pendingPlacementSave = bounds;
            if (_placementSaveTask == null || _placementSaveTask.IsCompleted)
            {
                _placementSaveTask = ProcessPlacementSaveAsync();
            }
        }
    }

    private async Task ProcessPlacementSaveAsync()
    {
        while (true)
        {
            PixelRect? target;
            lock (_gate)
            {
                target = _pendingPlacementSave;
                _pendingPlacementSave = null;
            }

            if (!target.HasValue)
            {
                break;
            }

            try
            {
                await _settingsService.UpdateAsync(s => s with
                {
                    TranslateWindow = s.TranslateWindow with { LastBounds = target.Value }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogPlacementSaveFailed(_logger, ex.GetType().Name);
            }
        }
    }

    internal void OnWindowActivated()
    {
        lock (_gate)
        {
            _isActiveSnapshot = true;
        }
    }

    internal Task OnWindowDeactivated()
    {
        lock (_gate)
        {
            _isActiveSnapshot = false;
            if (_deactivationSuppressionCount > 0)
            {
                return Task.CompletedTask;
            }
        }

        if (!_isShuttingDown && _viewModel != null && !_viewModel.IsPinned && _viewModel.HideOnDeactivate)
        {
            return HideAsync();
        }

        return Task.CompletedTask;
    }

    internal void OnWindowIsVisibleChanged(bool isVisible)
    {
        lock (_gate)
        {
            _isVisibleSnapshot = isVisible;
        }
    }

    internal IDisposable BeginCaptureSuppression()
    {
        lock (_gate)
        {
            _deactivationSuppressionCount++;
        }
        return new SuppressionScope(this);
    }

    private sealed class SuppressionScope(TranslateWindowPresenter presenter) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (presenter._gate)
                {
                    presenter._deactivationSuppressionCount--;
                }
            }
        }
    }

    internal void OnOperationRetired(TranslateWindowOperation op)
    {
        lock (_gate)
        {
            _activeOperations.Remove(op);
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_shutdownTask != null && !_shutdownTask.IsCompleted)
            {
                return _shutdownTask;
            }

            _isShuttingDown = true;
            _shutdownTask = Task.Run(() => ShutdownCoreAsync(cancellationToken), CancellationToken.None);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        List<TranslateWindowOperation> opsToCancel;
        Task? placementTask;
        lock (_gate)
        {
            opsToCancel = _activeOperations.ToList();
            placementTask = _placementSaveTask;
        }

        foreach (var op in opsToCancel)
        {
            op.Cancel();
        }

        if (placementTask != null)
        {
            try
            {
                await placementTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_viewModel != null)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                _viewModel.OnWindowHidden();
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);

            try
            {
                await _viewModel.WaitForActiveWorkAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogShutdownError(_logger, ex.GetType().Name);
                throw;
            }
        }

        // 退出屏障：等待所有租约（包括 caller 仍持有的）被释放归还
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_activeOperations.Count == 0)
                {
                    break;
                }
            }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_window != null)
            {
                bool closedFired = false;
                void OnClosed(object? s, EventArgs e) => closedFired = true;
                _window.Closed += OnClosed;

                try
                {
                    _window.ForceClose = true;
                    _window.Close();
                }
                finally
                {
                    _window.Closed -= OnClosed;
                }

                bool isRetained = (System.Windows.Application.Current != null &&
                    System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().Contains(_window))
                    || new WindowInteropHelper(_window).Handle != IntPtr.Zero;

                if (!closedFired && isRetained)
                {
                    throw new InvalidOperationException("翻译窗口关闭失败或被取消，资源仍保持存活。");
                }

                _window = null;
            }

            if (_viewModel != null)
            {
                _viewModel.Dispose();
                _viewModel = null;
            }

            if (_scope != null)
            {
                _scope.Dispose();
                _scope = null;
            }

            lock (_gate)
            {
                _isVisibleSnapshot = false;
                _isActiveSnapshot = false;
            }

            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "保存最后位置失败：{ErrorType}")]
    private static partial void LogPlacementSaveFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "翻译窗口退出收尾异常：{ErrorType}")]
    private static partial void LogShutdownError(ILogger logger, string errorType);

    internal void LogOperationCancelError(string errorType) => LogOperationCancelError(_logger, errorType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "操作租约取消异常：{ErrorType}")]
    private static partial void LogOperationCancelError(ILogger logger, string errorType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "展示翻译窗口失败：{ErrorType}")]
    private static partial void LogShowFailed(ILogger logger, string errorType);
}
