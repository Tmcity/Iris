using Iris.App.Hosting;
using Iris.App.Settings;
using Iris.App.Updates;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.App.Services;

/// <summary>Owns shutdown and the confirmed update after its per-window scope transfers it.</summary>
internal sealed partial class AppLifetime : IAppLifetime
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<AppLifetime> _logger;
    private readonly Lock _gate = new();
    private PreparedUpdate? _confirmedUpdate;
    private bool _shutdownRequested;
    private readonly List<Exception> _recoveryFailures = [];
    internal Task? ShutdownWork { get; private set; }
    internal Exception? RecoveryFailure { get; private set; }
    internal IReadOnlyList<Exception> RecoveryFailures { get { lock (_gate) return _recoveryFailures.ToArray(); } }
    internal UpdateRecoveryWindow? RecoveryWindow { get; private set; }
    internal bool HasConfirmedUpdate { get { lock (_gate) return _confirmedUpdate != null; } }

    public AppLifetime(IServiceProvider serviceProvider, IUiDispatcher ui, ILogger<AppLifetime> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _ui = ui; _logger = logger;
    }

    internal bool TryRequestUpdateShutdown(object owner, PreparedUpdate prepared)
    {
        lock (_gate)
        {
            if (_shutdownRequested || _confirmedUpdate != null || ShutdownWork != null
                || _serviceProvider.GetRequiredService<UpdateHandoff>().IsPrepared != true
                || !_serviceProvider.GetRequiredService<AppUpdateCoordinator>().TryTransferApply(prepared, owner)) return false;
            _confirmedUpdate = prepared;
            StartShutdownLocked(retryUpdate: false, cancelUpdate: false);
            return true;
        }
    }

    public void RequestShutdown()
    {
        lock (_gate)
        {
            if (ShutdownWork is { IsCompleted: false } || ShutdownWork?.IsCompletedSuccessfully == true) return;
            if (_confirmedUpdate != null && RecoveryFailure != null)
            {
                Observe(ShowRecoveryAsync(cleanupPending: false));
                return;
            }
            StartShutdownLocked(retryUpdate: false, cancelUpdate: false);
        }
    }

    internal Task RetryUpdateAsync()
    {
        lock (_gate)
        {
            if (ShutdownWork is { IsCompleted: false }) return ShutdownWork;
            if (_confirmedUpdate == null || RecoveryFailure == null) return Task.CompletedTask;
            return StartShutdownLocked(retryUpdate: true, cancelUpdate: false);
        }
    }

    internal Task CancelUpdateAndExitAsync()
    {
        lock (_gate)
        {
            if (ShutdownWork is { IsCompleted: false }) return ShutdownWork;
            if (_confirmedUpdate == null || RecoveryFailure == null) return Task.CompletedTask;
            return StartShutdownLocked(retryUpdate: false, cancelUpdate: true);
        }
    }

    private Task StartShutdownLocked(bool retryUpdate, bool cancelUpdate)
    {
        _shutdownRequested = true;
        ShutdownWork = ShutdownAsync(retryUpdate, cancelUpdate);
        Observe(ShutdownWork);
        return ShutdownWork;
    }

    private void Observe(Task work) => _ = work.ContinueWith(done => LogShutdownFailed(_logger, done.Exception!.GetType().Name),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task ShutdownAsync(bool retryUpdate, bool cancelUpdate)
    {
        // Exit's Dispatcher task and the transferring install command must finish
        // before shutdown drains their accepted per-window/IPC work.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var handoff = _serviceProvider.GetService<UpdateHandoff>();
        var updates = _serviceProvider.GetService<AppUpdateCoordinator>();
        PreparedUpdate? confirmed;
        lock (_gate) confirmed = _confirmedUpdate;
        try
        {
            await _ui.InvokeAsync(() => { RecoveryWindow?.SetBusy(true); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            if (confirmed != null && (retryUpdate || cancelUpdate))
            {
                // Retained revoke/cleanup is always joined before a new helper
                // may be prepared, even when the preceding revoke itself failed.
                await handoff!.RevokeAsync().ConfigureAwait(false);
                await updates!.StopAsync().ConfigureAwait(false);
                if (cancelUpdate)
                {
                    updates.CancelTransferredApply(confirmed);
                    lock (_gate) _confirmedUpdate = null;
                    confirmed = null;
                    await _ui.InvokeAsync(() => { RecoveryWindow?.Close(); RecoveryWindow = null; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    using var validation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await updates.ValidateTransferredApplyAsync(confirmed, validation.Token).ConfigureAwait(false);
                    await handoff.PrepareAsync(confirmed.Candidate, validation.Token).ConfigureAwait(false);
                }
            }

            await _ui.InvokeAsync(() => _serviceProvider.GetRequiredService<AppBootstrapper>().StopAsync(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            var errors = new List<Exception> { primary };
            try { if (handoff != null) await handoff.RevokeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { errors.Add(cleanup); }
            bool cleanupPending = errors.Count != 1;

            if (confirmed != null)
            {
                try { updates!.ReportTransferredApplyFailure(confirmed, ErrorCode.Unknown); }
                catch (Exception cleanup) { errors.Add(cleanup); }
                try
                {
                    var settings = _serviceProvider.GetService<SettingsWindowPresenter>();
                    if (settings != null) await settings.ReportUpdateShutdownFailureAsync(updates!.Snapshot).ConfigureAwait(false);
                }
                catch (Exception cleanup) { errors.Add(cleanup); }
            }
            RecoveryFailure = errors.Count == 1 ? primary : new AggregateException(errors);
            try
            {
                if (confirmed != null) await ShowRecoveryAsync(cleanupPending).ConfigureAwait(false);
                else _serviceProvider.GetService<INotificationService>()?.Notify(NotificationLevel.Error, UiText.AppName, UiText.ShutdownIncomplete);
            }
            catch (Exception feedback) { errors.Add(feedback); }
            RecoveryFailure = errors.Count == 1 ? primary : new AggregateException(errors);
            lock (_gate) _recoveryFailures.Add(RecoveryFailure);
            if (errors.Count > 1) throw RecoveryFailure;
            throw;
        }

        // Main still owns the prepared handoff and commits only after Host,
        // logging and the original instance mutex have all been released.
        lock (_gate) { _confirmedUpdate = null; RecoveryFailure = null; }
        if (RecoveryWindow != null && System.Windows.Application.Current?.Dispatcher.HasShutdownStarted != true)
        {
            try { await _ui.InvokeAsync(() => { RecoveryWindow?.Close(); RecoveryWindow = null; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false); }
            catch (OperationCanceledException) when (System.Windows.Application.Current?.Dispatcher.HasShutdownStarted == true) { }
        }
    }

    private Task ShowRecoveryAsync(bool cleanupPending) => _ui.InvokeAsync(() =>
    {
        PreparedUpdate? confirmed;
        lock (_gate) confirmed = _confirmedUpdate;
        if (confirmed == null) return Task.CompletedTask;
        if (RecoveryWindow == null)
        {
            var window = new UpdateRecoveryWindow(RetryUpdateAsync, CancelUpdateAndExitAsync);
            window.Closed += (_, _) => { if (ReferenceEquals(RecoveryWindow, window)) RecoveryWindow = null; };
            RecoveryWindow = window;
        }
        RecoveryWindow.ShowFailure(confirmed.Candidate.Version, cleanupPending);
        return Task.CompletedTask;
    }, CancellationToken.None);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Application shutdown incomplete: {ExceptionType}")]
    private static partial void LogShutdownFailed(ILogger logger, string exceptionType);
}
