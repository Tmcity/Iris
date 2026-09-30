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

        await _uiDispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (_isShuttingDown)
                {
                    return Task.CompletedTask;
                }

                if (_activeSession != null && _activeSession.IsOpen)
                {
                    _activeSession.ActivateAndNavigate(targetPage);
                    return Task.CompletedTask;
                }

                // 首次 Show 或关闭后重新 Show：在 UI 线程创建独立 DI scope
                var scope = _serviceProvider.CreateScope();
                try
                {
                    var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
                    var viewModel = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                    var themeService = scope.ServiceProvider.GetRequiredService<ThemeService>();
                    var saveCoordinator = scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>();

                    window.DataContext = viewModel;

                    var session = new SettingsWindowSession(scope, window, viewModel, themeService, saveCoordinator);
                    _activeSession = session;

                    window.Closed += (sender, args) =>
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_activeSession, session))
                            {
                                _activeSession = null;
                            }
                        }
                    };

                    window.Show();
                    session.ActivateAndNavigate(targetPage);
                    return Task.CompletedTask;
                }
                catch
                {
                    _activeSession = null;
                    scope.Dispose();
                    throw;
                }
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private Task? _shutdownTask;

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_shutdownTask != null)
            {
                return _shutdownTask;
            }

            _isShuttingDown = true;
            var sessionToShutdown = _activeSession;
            _activeSession = null;

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
