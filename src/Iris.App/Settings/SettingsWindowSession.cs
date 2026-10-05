using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Iris.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.App.Settings;

/// <summary>
/// 管理单次设置窗口打开的独立会话身份、DI 作用域、草稿与工作所有权。
/// 完整规则见 SETTINGS_EDITING.md §2。
/// </summary>
internal sealed class SettingsWindowSession : IDisposable
{
    private readonly IServiceScope _scope;
    private readonly SettingsWindow _window;
    private readonly SettingsViewModel _viewModel;
    private readonly ThemeService _themeService;
    private readonly GeneralSettingsSaveCoordinator _saveCoordinator;
    private readonly Lock _gate = new();

    private bool _isClosed;
    private bool _disposed;
    private Task? _cleanupTask;

    public event Action<SettingsWindowSession>? CleanupCompleted;
    internal event Action? DisplayIntent;
    private readonly Iris.App.Capture.WindowPreparationIntent? _preparationIntent;

    public SettingsWindowSession(
        IServiceScope scope,
        SettingsWindow window,
        SettingsViewModel viewModel,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        Iris.App.Capture.WindowPreparationIntent? preparationIntent = null)
    {
        _preparationIntent = preparationIntent;
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _saveCoordinator = saveCoordinator ?? throw new ArgumentNullException(nameof(saveCoordinator));

        _window.Closing += OnWindowClosing;
        _window.Closed += OnWindowClosed;
        _viewModel.RequestClose += OnViewModelRequestClose;
    }

    private void OnViewModelRequestClose()
    {
        _ = ShutdownAsync(CancellationToken.None);
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_isClosed && !_disposed;
            }
        }
    }

    public SettingsWindow Window => _window;
    public SettingsViewModel ViewModel => _viewModel;

    public Task? CurrentCleanupTask
    {
        get
        {
            lock (_gate)
            {
                return _cleanupTask;
            }
        }
    }

    public void ActivateAndNavigate(string pageKey)
    {
        lock (_gate)
        {
            if (_isClosed || _disposed)
            {
                return;
            }
        }

        var attempt = _preparationIntent?.BeginShow();
        bool shown = false;
        try
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
            _window.Activate();
            _viewModel.CurrentPageKey = pageKey;
            shown = true;
        }
        finally { attempt?.Complete(_window, shown); }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_window.ForceClose)
        {
            return;
        }

        // 仅当存在活跃录入、录入清理失败、正在保存、或已有异步清理进行中时，拦截立即销毁以保护界面与重试
        if (_viewModel.IsAnyRecording || _viewModel.HasFailedRecordingCleanup || _viewModel.IsSaving || (_cleanupTask != null && !_cleanupTask.IsCompleted))
        {
            e.Cancel = true;
            _ = ShutdownAsync(CancellationToken.None);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DisplayIntent?.Invoke();
        lock (_gate)
        {
            _isClosed = true;
        }
        _ = ShutdownAsync(CancellationToken.None);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        DisplayIntent?.Invoke();
        lock (_gate)
        {
            if (_cleanupTask != null && !_cleanupTask.IsFaulted && !_cleanupTask.IsCanceled)
            {
                return _cleanupTask;
            }

            _cleanupTask = CleanupCoreAsync(cancellationToken);
            return _cleanupTask;
        }
    }

    private async Task CleanupCoreAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // 1. 停止接纳新工作并取消提交前工作
        await InvokeOnUiAsync(() =>
        {
            _saveCoordinator.StopAcceptanceAndCancelPreCommit();
            _viewModel.StopAcceptance();
        }).ConfigureAwait(false);

        // 2. 等待协调器中所有已接纳的后台保存/重试/查询任务完成
        await _saveCoordinator.WaitForActiveWorkAsync().ConfigureAwait(false);

        // 3. 等待 ViewModel 中所有已接纳的保存/重试/查询任务及 UI 收尾完成
        await _viewModel.WaitForActiveWorkAsync().ConfigureAwait(false);

        // Window and bound VM notifications remain on their owning dispatcher even when App exit
        // resumes on a worker after draining preparation/platform work.
        await InvokeOnUiAsync(() =>
        {
            EnsureWindowClosedOnUi();
            DisposeInternal();
        }).ConfigureAwait(false);
    }

    private Task InvokeOnUiAsync(Action action)
    {
        if (_window.Dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return _window.Dispatcher.InvokeAsync(action).Task;
    }

    private void EnsureWindowClosedOnUi()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_isClosed)
            {
                return;
            }
        }

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

        if (!closedFired)
        {
            throw new InvalidOperationException("设置窗口关闭被取消或失败，会话保持存活");
        }

        lock (_gate)
        {
            _isClosed = true;
        }
    }

    private void DisposeInternal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _isClosed = true;
        }

        _window.Closing -= OnWindowClosing;
        _window.Closed -= OnWindowClosed;
        _viewModel.RequestClose -= OnViewModelRequestClose;

        try
        {
            _themeService.ClearPreview(_viewModel.SessionOwner);
        }
        catch
        {
        }

        try
        {
            _viewModel.Dispose();
        }
        catch
        {
        }

        try
        {
            _scope.Dispose();
        }
        catch
        {
        }

        CleanupCompleted?.Invoke(this);
        DisplayIntent = null;
    }

    public void Dispose()
    {
        DisposeInternal();
    }
}
