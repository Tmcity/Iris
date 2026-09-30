using System;
using System.Threading;
using System.Threading.Tasks;
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
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                return;
            }
            oldSession = _activeSession;
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

        SettingsWindowSession? activeExisting = null;
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                return;
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
                    return;
                }
                if (_activeSession != null && _activeSession.IsOpen)
                {
                    _activeSession.ActivateAndNavigate(targetPage);
                    return;
                }
            }

            IServiceScope? scope = null;
            SettingsWindow? window = null;
            SettingsWindowSession? session = null;
            GeneralSettingsSaveCoordinator? saveCoordinator = null;
            SettingsViewModel? viewModel = null;

            try
            {
                scope = _serviceProvider.CreateScope();
                window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
                viewModel = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                var themeService = scope.ServiceProvider.GetRequiredService<ThemeService>();
                saveCoordinator = scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();

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
                else
                {
                    if (window != null)
                    {
                        window.ForceClose = true;
                        try { window.Close(); } catch { }
                    }
                    if (saveCoordinator != null)
                    {
                        try { await saveCoordinator.WaitForActiveWorkAsync().ConfigureAwait(false); } catch { }
                    }
                    if (viewModel != null)
                    {
                        try { await viewModel.WaitForActiveWorkAsync().ConfigureAwait(false); } catch { }
                    }
                    scope?.Dispose();
                }

                lock (_gate)
                {
                    if (ReferenceEquals(_activeSession, session))
                    {
                        _activeSession = null;
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

            if (_activeSession == null)
            {
                _isShuttingDown = true;
                _shutdownTask = Task.CompletedTask;
                return _shutdownTask;
            }

            _isShuttingDown = true;
            var sessionToShutdown = _activeSession;

            _shutdownTask = ShutdownCoreAsync(sessionToShutdown, cancellationToken);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(SettingsWindowSession? sessionToShutdown, CancellationToken cancellationToken)
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
        lock (_gate)
        {
            currentSession = _activeSession ?? sessionToShutdown;
        }

        if (currentSession != null)
        {
            try
            {
                await currentSession.ShutdownAsync(cancellationToken).ConfigureAwait(false);
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
                    if (ReferenceEquals(_activeSession, currentSession) && !currentSession.IsOpen)
                    {
                        _activeSession = null;
                    }
                }
            }
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
        // 本单仅开放 general；其它尚未开放项统一回到 general，不抛异常也不记录原始 key
        return trimmed.Equals("general", StringComparison.OrdinalIgnoreCase) ? "general" : "general";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "设置窗口关闭时发生异常：{ExceptionType}")]
    private static partial void LogSettingsShutdownError(ILogger logger, string exceptionType);
}
