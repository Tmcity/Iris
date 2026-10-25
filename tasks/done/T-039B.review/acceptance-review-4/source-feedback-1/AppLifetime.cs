using System;
using System.Collections.Generic;
using System.Windows;
using Iris.App.Hosting;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.App.Services;

/// <summary>
/// 应用程序生命周期服务：协调全系统的优雅退出。
/// </summary>
internal sealed partial class AppLifetime : IAppLifetime
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<AppLifetime> _logger;
    private readonly Lock _gate = new();
    internal Task? ShutdownWork { get; private set; }

    public AppLifetime(IServiceProvider serviceProvider, IUiDispatcher ui, ILogger<AppLifetime> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _ui = ui; _logger = logger;
    }

    public void RequestShutdown()
    {
        lock (_gate)
        {
            if (ShutdownWork is { IsCompleted: false } || ShutdownWork?.IsCompletedSuccessfully == true) return;
            ShutdownWork = ShutdownAsync();
            _ = ShutdownWork.ContinueWith(done => LogShutdownFailed(_logger, done.Exception!.GetType().Name),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
    private async Task ShutdownAsync()
    {
        // Exit's Dispatcher task must be able to finish before shutdown drains accepted commands.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await _ui.InvokeAsync(() => _serviceProvider.GetRequiredService<AppBootstrapper>().StopAsync(), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception primary)
        {
            var cleanupFailures = new List<Exception>();
            var handoff = _serviceProvider.GetService<Iris.App.Updates.UpdateHandoff>();
            try
            {
                if (handoff != null) await handoff.RevokeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) { cleanupFailures.Add(cleanup); }

            var coordinator = _serviceProvider.GetService<Iris.App.Updates.AppUpdateCoordinator>();
            bool recoveryAvailable = false;
            try { recoveryAvailable = coordinator?.RollbackLastApply(ErrorCode.Unknown) == true; }
            catch (Exception cleanup) { cleanupFailures.Add(cleanup); }

            var presenter = _serviceProvider.GetService<Iris.App.Settings.SettingsWindowPresenter>();
            if (recoveryAvailable && presenter != null)
            {
                try
                {
                    await _ui.InvokeAsync(async () =>
                    {
                        // The presenter is the owner of the real scoped VM. If the
                        // previous settings session survived, resume that VM; if it
                        // was already cleaned up, ShowAsync creates a new scope.
                        presenter.ResumeUpdateRecovery();
                        await presenter.ShowAsync("general").ConfigureAwait(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
            }

            try
            {
                var notifications = _serviceProvider.GetService<INotificationService>();
                notifications?.Notify(NotificationLevel.Error, UiText.AppName,
                    recoveryAvailable ? UiText.UpdateRecoveryRequired : UiText.ShutdownIncomplete);
            }
            catch (Exception cleanup) { cleanupFailures.Add(cleanup); }

            if (cleanupFailures.Count != 0)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(cleanupFailures);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            throw new InvalidOperationException("Shutdown failure was not rethrown.");
        }
    }
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Application shutdown incomplete: {ExceptionType}")]
    private static partial void LogShutdownFailed(ILogger logger, string exceptionType);
}
