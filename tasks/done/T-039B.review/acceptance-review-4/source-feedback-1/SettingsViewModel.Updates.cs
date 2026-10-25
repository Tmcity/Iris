using System.Windows;
using System.Windows.Input;
using Iris.App.Services;
using Iris.App.Updates;
using Iris.Core.Errors;
using Iris.Core.Presentation;

namespace Iris.App.Settings;

internal sealed partial class SettingsViewModel
{
    private AppUpdateCoordinator? _updates;
    private UpdateHandoff? _handoff;
    private IAppLifetime? _updateLifetime;
    private CancellationTokenSource _updateWindowClosing = new();
    private UpdateSnapshot? _updateSnapshot;
    private bool _updateHandoffTransferred;
    private bool _updateRecoveryMode;
    private Task? _updateCancelTask;
    private bool _updateCancelJoined;
    public string CurrentVersion => _updateSnapshot?.CurrentVersion ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public string UpdateStatus => UiText.UpdateState(_updateSnapshot?.Phase ?? UpdatePhase.Unsupported);
    public string UpdateDetail => _updateSnapshot?.Error is ErrorCode code ? ErrorMessages.GetDefault(code) : _updateSnapshot?.NewVersion ?? "";
    public int UpdateProgress => _updateSnapshot?.Progress ?? 0;
    public ICommand CheckUpdateCommand { get; private set; } = null!;
    public ICommand DownloadUpdateCommand { get; private set; } = null!;
    public ICommand CancelUpdateCommand { get; private set; } = null!;
    public ICommand InstallUpdateCommand { get; private set; } = null!;
    private void InitializeUpdates(AppUpdateCoordinator? coordinator, UpdateHandoff? handoff, IAppLifetime? lifetime)
    {
        _updates = coordinator; _handoff = handoff; _updateLifetime = lifetime; _updateSnapshot = coordinator?.Snapshot;
        _updateRecoveryMode = coordinator?.IsRecoveryAvailable == true;
        bool Active() => !_disposed && !_isStopped && _updates != null;
        bool RecoveryActive() => !_disposed && _updateRecoveryMode && _updates != null && _updates.IsRecoveryAvailable;
        CheckUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(() => _updates!.CheckAsync(this)),
            () => Active() && !_updates!.IsAcceptanceClosed && _updateSnapshot?.Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.NoRelease or UpdatePhase.Failed or UpdatePhase.Available or UpdatePhase.Ready);
        DownloadUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(() => _updates!.DownloadAsync(this, _updateSnapshot!.OfferId!.Value)),
            () => Active() && !_updates!.IsAcceptanceClosed && _updateSnapshot?.Phase == UpdatePhase.Available);
        CancelUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(() => _updates!.CancelAsync()),
            () => Active() && _updateSnapshot?.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.CleanupPending);
        InstallUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(InstallUpdateAsync, allowRecovery: true),
            () => (Active() || RecoveryActive()) && _handoff != null && _updateLifetime != null && (_updateSnapshot?.Phase == UpdatePhase.Ready || (_updateSnapshot?.Phase == UpdatePhase.Failed && _updates?.HasCandidate == true)));
        if (_updates != null) _updates.Changed += OnUpdateChanged;
    }
    private void OnUpdateChanged(object? sender, EventArgs args)
    {
        _ = UpdateWorkAsync(() => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed && !_isStopped)
            {
                _updateSnapshot = _updates?.Snapshot;
                if (_updateSnapshot?.Phase is UpdatePhase.Failed or UpdatePhase.Ready or UpdatePhase.Idle)
                {
                    lock (_admissionLock) _updateHandoffTransferred = false;
                }
                OnPropertyChanged(nameof(UpdateStatus)); OnPropertyChanged(nameof(UpdateDetail));
                OnPropertyChanged(nameof(CurrentVersion)); OnPropertyChanged(nameof(UpdateProgress));
                foreach (var command in new[] { CheckUpdateCommand, DownloadUpdateCommand, CancelUpdateCommand, InstallUpdateCommand })
                    (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
            return Task.CompletedTask;
        }, CancellationToken.None));
    }
    private async Task UpdateWorkAsync(Func<Task> action, bool allowRecovery = false)
    {
        TaskCompletionSource completion;
        lock (_admissionLock)
        {
            if ((_isStopped && !(allowRecovery && _updateRecoveryMode)) || _disposed) return;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _activeWork.Add(completion.Task);
        }
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_isStopped && !_disposed) HandleCommandException("Update", ex);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_admissionLock) _activeWork.Remove(completion.Task);
            completion.TrySetResult();
        }
    }
    private async Task InstallUpdateAsync()
    {
        if (_updates == null || _handoff == null || _updateLifetime == null || _updateSnapshot?.OfferId is not Guid id) return;
        if (MessageBox.Show(UiText.UpdateInstallConfirm, UiText.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var candidate = await _updates.PrepareApplyAsync(this, id, _updateWindowClosing.Token).ConfigureAwait(false);
        if (candidate == null) return;
        try
        {
            await _handoff.PrepareAsync(candidate, _updateWindowClosing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_updateWindowClosing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                IrisException ie => ie.Code,
                TimeoutException => ErrorCode.Timeout,
                _ => ErrorCode.Unknown
            };
            _updates.RollbackApply(candidate, code);
            LogCommandUnexpectedFailed(_logger, "InstallUpdate", ex.GetType().Name);
            return;
        }
        // No settings save is implicit. RequestShutdown yields before waiting for this window's active work.
        lock (_admissionLock)
        {
            _updateWindowClosing.Token.ThrowIfCancellationRequested();
            _updateHandoffTransferred = true;
            _updateLifetime.RequestShutdown();
        }
    }
    /// <summary>
    /// Reopens only the local prepared-update recovery action after application
    /// shutdown failed. Ordinary settings and network work remains stopped.
    /// Must be called by the owning SettingsWindowPresenter on the WPF dispatcher.
    /// </summary>
    internal void ResumeUpdateRecovery()
    {
        if (_disposed || _updates?.IsRecoveryAvailable != true) return;
        lock (_admissionLock)
        {
            _updateRecoveryMode = true;
            _updates.Changed -= OnUpdateChanged;
            _updates.Changed += OnUpdateChanged;
            if (_updateWindowClosing.IsCancellationRequested)
            {
                _updateWindowClosing.Dispose();
                _updateWindowClosing = new CancellationTokenSource();
            }
            _updateCancelTask = null;
            _updateCancelJoined = false;
            _updateSnapshot = _updates.Snapshot;
        }
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(UpdateDetail));
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(UpdateProgress));
        foreach (var command in new[] { CheckUpdateCommand, DownloadUpdateCommand, CancelUpdateCommand, InstallUpdateCommand })
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    internal bool UpdateHandoffTransferred
    {
        get
        {
            lock (_admissionLock) return _updateHandoffTransferred;
        }
    }
    internal void ResetHandoffTransferred()
    {
        lock (_admissionLock)
        {
            _updateHandoffTransferred = false;
        }
    }
    internal void SetHandoffTransferredForTesting(bool transferred)
    {
        lock (_admissionLock)
        {
            _updateHandoffTransferred = transferred;
        }
    }
    private void SealUpdates()
    {
        if (_updates != null) _updates.Changed -= OnUpdateChanged;
        _updateCancelTask ??= _updateWindowClosing.CancelAsync();
    }
    private async Task CloseUpdatesAsync()
    {
        var errors = new List<Exception>();
        if (_updateCancelTask != null && !_updateCancelJoined)
        {
            try { await _updateCancelTask.ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
            finally { _updateCancelJoined = true; }
        }
        try { if (_handoff?.IsPrepared == true && !_updateHandoffTransferred) await _handoff.RevokeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        try
        {
            if (_updates != null)
            {
                await _updates.CloseOwnerAsync(this).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
