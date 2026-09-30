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

    public async Task ShowAsync(string? pageKey = null)
    {
        var targetPage = NormalizePageKey(pageKey);

        await _uiDispatcher.InvokeAsync(async () =>
        {
            SettingsWindowSession session;
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

            SettingsWindowSession? oldSession;
            lock (_gate)
            {
                oldSession = _activeSession;
            }
            if (oldSession != null)
            {
                try
                {
                    await oldSession.ShutdownAsync().ConfigureAwait(true);
                }
                catch
                {
                }
            }

            lock (_gate)
            {
                if (_isShuttingDown)
                {
                    return;
                }
            }

            var scope = _serviceProvider.CreateScope();
            SettingsWindow? window = null;
            try
            {
                window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
                var viewModel = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                var themeService = scope.ServiceProvider.GetRequiredService<ThemeService>();
                var saveCoordinator = scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();

                window.DataContext = viewModel;

                session = new SettingsWindowSession(scope, window, viewModel, themeService, saveCoordinator);
                session.CleanupCompleted += OnSessionCleanupCompleted;

                lock (_gate)
                {
                    _activeSession = session;
                }

                window.Show();
                session.ActivateAndNavigate(targetPage);
            }
            catch
            {
                if (window != null)
                {
                    window.ForceClose = true;
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }

                if (_activeSession != null)
                {
                    _ = _activeSession.ShutdownAsync();
                }
                else
                {
                    scope.Dispose();
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
            if (_shutdownTask != null)
            {
                if (_shutdownTask.IsCompleted && _activeSession != null && _activeSession.IsOpen)
                {
                    _shutdownTask = null;
                }
                else
                {
                    return _shutdownTask;
                }
            }

            _isShuttingDown = true;
            var sessionToShutdown = _activeSession;

            _shutdownTask = ShutdownCoreAsync(sessionToShutdown, cancellationToken);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(SettingsWindowSession? sessionToShutdown, CancellationToken cancellationToken)
    {
        if (sessionToShutdown != null)
        {
            try
            {
                await sessionToShutdown.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSettingsShutdownError(_logger, ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeSession, sessionToShutdown) && !sessionToShutdown.IsOpen)
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "设置窗口关闭时发生异常")]
    private static partial void LogSettingsShutdownError(ILogger logger, Exception ex);
}
