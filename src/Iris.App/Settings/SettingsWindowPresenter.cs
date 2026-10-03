using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.App.Settings;

internal interface ISettingsShutdownCoordinator
{
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 设置窗口展示器（App 层单例实现）。
/// 完整规则见 SETTINGS_EDITING.md §2 与 §7。
/// </summary>
internal sealed partial class SettingsWindowPresenter : ISettingsWindowPresenter, ISettingsShutdownCoordinator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<SettingsWindowPresenter> _logger;

    private readonly Lock _gate = new();
    private SettingsWindowSession? _activeSession;
    private PartialWindowCleanup? _pendingRollback;
    private bool _isShuttingDown;
    private Task? _shutdownTask;
    private Task? _openTask;

    public SettingsWindowPresenter(
        IServiceProvider serviceProvider,
        IUiDispatcher uiDispatcher,
        ILogger<SettingsWindowPresenter> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal SettingsWindowSession? ActiveSession
    {
        get
        {
            lock (_gate)
            {
                return _activeSession;
            }
        }
    }

    internal bool HasPendingRollback
    {
        get
        {
            lock (_gate)
            {
                return _pendingRollback != null && _pendingRollback.IsAlive;
            }
        }
    }

    internal Task? OpenTask
    {
        get
        {
            lock (_gate)
            {
                return _openTask;
            }
        }
    }

    public Task ShowAsync(string? pageKey = null)
    {
        var targetPage = NormalizePageKey(pageKey);

        lock (_gate)
        {
            if (_isShuttingDown)
            {
                return Task.CompletedTask;
            }

            if (_activeSession != null && _activeSession.IsOpen)
            {
                var existing = _activeSession;
                return _uiDispatcher.InvokeAsync(() =>
                {
                    existing.ActivateAndNavigate(targetPage);
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }

            if (_openTask != null && !_openTask.IsCompleted)
            {
                return AwaitAndNavigateAsync(_openTask, targetPage);
            }

            _openTask = ShowCoreAsync(targetPage);
            return _openTask;
        }
    }

    private async Task AwaitAndNavigateAsync(Task runningOpenTask, string targetPage)
    {
        await runningOpenTask.ConfigureAwait(false);

        lock (_gate)
        {
            if (_isShuttingDown || _activeSession == null || !_activeSession.IsOpen)
            {
                return;
            }
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (_isShuttingDown || _activeSession == null || !_activeSession.IsOpen)
                {
                    return Task.CompletedTask;
                }
                _activeSession.ActivateAndNavigate(targetPage);
                return Task.CompletedTask;
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ShowCoreAsync(string targetPage)
    {
        SettingsWindowSession? oldSession;
        PartialWindowCleanup? oldRollback;
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                throw new OperationCanceledException("Presenter is shutting down.");
            }
            oldSession = _activeSession;
            oldRollback = _pendingRollback;
        }

        if (oldSession != null)
        {
            try
            {
                await oldSession.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSettingsShutdownError(_logger, ex.GetType().Name);
                throw;
            }
        }

        if (oldRollback != null && oldRollback.IsAlive)
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() => oldRollback.CleanupAsync(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSettingsShutdownError(_logger, ex.GetType().Name);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (!oldRollback.IsAlive && ReferenceEquals(_pendingRollback, oldRollback))
                    {
                        _pendingRollback = null;
                    }
                }
            }
        }

        SettingsWindowSession? activeExisting = null;
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                throw new OperationCanceledException("Presenter is shutting down.");
            }
            if (_activeSession != null && _activeSession.IsOpen)
            {
                activeExisting = _activeSession;
            }
        }

        if (activeExisting != null)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                activeExisting.ActivateAndNavigate(targetPage);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await _uiDispatcher.InvokeAsync(async () =>
        {
            lock (_gate)
            {
                if (_isShuttingDown)
                {
                    throw new OperationCanceledException("Presenter is shutting down.");
                }
                if (_activeSession != null && _activeSession.IsOpen)
                {
                    _activeSession.ActivateAndNavigate(targetPage);
                    return;
                }
                if (_pendingRollback != null && _pendingRollback.IsAlive)
                {
                    throw new InvalidOperationException("前次设置窗口清理尚未完成，无法打开新窗口");
                }
            }

            IServiceScope? scope = null;
            SettingsWindow? window = null;
            SettingsWindowSession? session = null;
            GeneralSettingsSaveCoordinator? saveCoordinator = null;
            SettingsViewModel? viewModel = null;
            PartialWindowCleanup? partialCleanup = null;

            try
            {
                scope = _serviceProvider.CreateScope();
                partialCleanup = new PartialWindowCleanup(scope);
                lock (_gate)
                {
                    _pendingRollback = partialCleanup;
                }

                window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
                partialCleanup.SetWindow(window);

                viewModel = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                partialCleanup.SetViewModel(viewModel);

                var themeService = scope.ServiceProvider.GetRequiredService<ThemeService>();
                saveCoordinator = scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();
                partialCleanup.SetSaveCoordinator(saveCoordinator);

                window.DataContext = viewModel;

                session = new SettingsWindowSession(scope, window, viewModel, themeService, saveCoordinator);
                session.CleanupCompleted += OnSessionCleanupCompleted;

                lock (_gate)
                {
                    if (_isShuttingDown)
                    {
                        throw new OperationCanceledException("Presenter is shutting down.");
                    }
                    _activeSession = session;
                    _pendingRollback = null;
                }

                window.Show();
                session.ActivateAndNavigate(targetPage);
            }
            catch
            {
                if (session != null)
                {
                    try
                    {
                        await session.ShutdownAsync().ConfigureAwait(true);
                    }
                    catch (Exception cleanupEx)
                    {
                        LogSettingsShutdownError(_logger, cleanupEx.GetType().Name);
                    }
                }
                else if (partialCleanup != null)
                {
                    try
                    {
                        await partialCleanup.CleanupAsync(CancellationToken.None).ConfigureAwait(true);
                    }
                    catch (Exception cleanupEx)
                    {
                        LogSettingsShutdownError(_logger, cleanupEx.GetType().Name);
                    }
                }

                lock (_gate)
                {
                    if (session != null && session.IsOpen)
                    {
                        _activeSession = session;
                    }
                    else if (ReferenceEquals(_activeSession, session))
                    {
                        _activeSession = null;
                    }

                    if (partialCleanup != null && partialCleanup.IsAlive)
                    {
                        _pendingRollback = partialCleanup;
                    }
                    else if (ReferenceEquals(_pendingRollback, partialCleanup))
                    {
                        _pendingRollback = null;
                    }
                }

                throw;
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnSessionCleanupCompleted(SettingsWindowSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
            }
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

            if (_activeSession == null && _pendingRollback == null && (_openTask == null || _openTask.IsCompleted))
            {
                _isShuttingDown = true;
                _shutdownTask = Task.CompletedTask;
                return _shutdownTask;
            }

            _isShuttingDown = true;
            var sessionToShutdown = _activeSession;
            var rollbackToShutdown = _pendingRollback;

            _shutdownTask = ShutdownCoreAsync(sessionToShutdown, rollbackToShutdown, cancellationToken);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(SettingsWindowSession? sessionToShutdown, PartialWindowCleanup? rollbackToShutdown, CancellationToken cancellationToken)
    {
        Task? openTask;
        lock (_gate)
        {
            openTask = _openTask;
        }

        if (openTask != null)
        {
            try
            {
                await openTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        SettingsWindowSession? currentSession;
        PartialWindowCleanup? currentRollback;
        lock (_gate)
        {
            currentSession = _activeSession ?? sessionToShutdown;
            currentRollback = _pendingRollback ?? rollbackToShutdown;
        }

        if (currentSession != null)
        {
            try
            {
                await currentSession.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _isShuttingDown = false;
                }
                LogSettingsShutdownError(_logger, ex.GetType().Name);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeSession, currentSession) && !currentSession.IsOpen)
                    {
                        _activeSession = null;
                    }
                }
            }
        }

        if (currentRollback != null && currentRollback.IsAlive)
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() => currentRollback.CleanupAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _isShuttingDown = false;
                }
                LogSettingsShutdownError(_logger, ex.GetType().Name);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (!currentRollback.IsAlive && ReferenceEquals(_pendingRollback, currentRollback))
                    {
                        _pendingRollback = null;
                    }
                }
            }
        }
    }

    private sealed class PartialWindowCleanup
    {
        private readonly IServiceScope _scope;
        private SettingsWindow? _window;
        private SettingsViewModel? _viewModel;
        private GeneralSettingsSaveCoordinator? _saveCoordinator;
        private bool _isClosed;
        private bool _isDisposed;

        public PartialWindowCleanup(IServiceScope scope)
        {
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        }

        public IServiceScope Scope => _scope;
        public SettingsWindow? Window => _window;
        public bool IsAlive => !_isDisposed;

        public void SetWindow(SettingsWindow window)
        {
            _window = window;
        }

        public void SetViewModel(SettingsViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public void SetSaveCoordinator(GeneralSettingsSaveCoordinator saveCoordinator)
        {
            _saveCoordinator = saveCoordinator;
        }

        public async Task CleanupAsync(CancellationToken cancellationToken = default)
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_isClosed && _window != null)
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

                bool isRetained = (Application.Current != null && Application.Current.Windows.Cast<Window>().Contains(_window))
                    || new WindowInteropHelper(_window).Handle != IntPtr.Zero;

                if (!closedFired && isRetained)
                {
                    throw new InvalidOperationException("设置窗口关闭被取消或失败，资源保持存活");
                }

                _isClosed = true;
            }

            if (_saveCoordinator != null)
            {
                try { await _saveCoordinator.WaitForActiveWorkAsync().ConfigureAwait(false); } catch { }
            }

            if (_viewModel != null)
            {
                try { await _viewModel.WaitForActiveWorkAsync().ConfigureAwait(false); } catch { }
                try { _viewModel.Dispose(); } catch { }
            }

            _scope.Dispose();
            _isDisposed = true;
        }
    }

    internal static string NormalizePageKey(string? pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            return "general";
        }

        var trimmed = pageKey.Trim();
        // 允许的既有集合：general / translation / ocr / hotkeys / selection / screenshot / floatingball / network / history / about
        // 本单开放 general 与 translation；其它尚未开放项统一回到 general，不抛异常也不记录原始 key
        if (trimmed.Equals("general", StringComparison.OrdinalIgnoreCase))
        {
            return "general";
        }
        if (trimmed.Equals("translation", StringComparison.OrdinalIgnoreCase))
        {
            return "translation";
        }
        if (trimmed.Equals("hotkeys", StringComparison.OrdinalIgnoreCase))
        {
            return "hotkeys";
        }
        return "general";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "设置窗口关闭时发生异常：{ExceptionType}")]
    private static partial void LogSettingsShutdownError(ILogger logger, string exceptionType);
}
