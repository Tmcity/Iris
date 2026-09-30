using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

    public SettingsWindowSession(
        IServiceScope scope,
        SettingsWindow window,
        SettingsViewModel viewModel,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _saveCoordinator = saveCoordinator ?? throw new ArgumentNullException(nameof(saveCoordinator));

        _window.Closed += OnWindowClosed;
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

    public void ActivateAndNavigate(string pageKey)
    {
        lock (_gate)
        {
            if (_isClosed || _disposed)
            {
                return;
            }
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
        _viewModel.CurrentPageKey = pageKey;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _isClosed = true;
        }
        Dispose();
    }

    private Task? _shutdownTask;

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_shutdownTask != null)
            {
                return _shutdownTask;
            }

            _shutdownTask = ShutdownCoreAsync(cancellationToken);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // 1. 停止接纳新工作并取消提交前工作
        _saveCoordinator.StopAcceptanceAndCancelPreCommit();

        // 2. 等待协调器中所有已接纳的后台保存/应用任务完成（直到释放 _gate）
        await _saveCoordinator.WaitForActiveWorkAsync().ConfigureAwait(false);

        // 3. 在 UI 线程安全关闭窗口并释放资源
        var dispatcher = _window.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(CloseAndCleanupCore).Task.ConfigureAwait(false);
        }
        else
        {
            CloseAndCleanupCore();
        }
    }

    private void CloseAndCleanupCore()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _isClosed = true;
        }

        try
        {
            _window.Closed -= OnWindowClosed;
            _window.ForceClose = true;
            if (_window.IsVisible)
            {
                _window.Close();
            }
        }
        catch
        {
        }

        Dispose();
    }

    public void Dispose()
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

        _themeService.ClearPreview(_viewModel.SessionOwner);
        _viewModel.Dispose();
        _scope.Dispose();
    }
}
