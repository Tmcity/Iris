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
    private readonly CancellationTokenSource _updateWindowClosing = new();
    private UpdateSnapshot? _updateSnapshot;
    private bool _updateHandoffTransferred;
    private bool _updateHandoffOwned;
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
        bool Active() => !_disposed && !_isStopped && _updates != null;
        CheckUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(() => _updates!.CheckAsync(this)),
            () => Active() && !_updates!.IsAcceptanceClosed && _updateSnapshot?.Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.NoRelease or UpdatePhase.Failed or UpdatePhase.Available or UpdatePhase.Ready);
        DownloadUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(() => _updates!.DownloadAsync(this, _updateSnapshot!.OfferId!.Value)),
            () => Active() && !_updates!.IsAcceptanceClosed && _updateSnapshot?.Phase == UpdatePhase.Available);
        CancelUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(() => _updates!.CancelAsync()),
            () => Active() && _updateSnapshot?.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.CleanupPending);
        InstallUpdateCommand = new AsyncRelayCommand(() => UpdateWorkAsync(InstallUpdateAsync),
            () => Active() && !_updates!.IsAcceptanceClosed && _handoff != null && _updateLifetime != null && (_updateSnapshot?.Phase == UpdatePhase.Ready || (_updateSnapshot?.Phase == UpdatePhase.Failed && _updates?.HasCandidate == true)));
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
    private async Task UpdateWorkAsync(Func<Task> action)
    {
        TaskCompletionSource completion;
        lock (_admissionLock)
        {
            if (_isStopped || _disposed) return;
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
    internal Func<bool> ConfirmUpdateInstall { get; set; } = () =>
        MessageBox.Show(UiText.UpdateInstallConfirm, UiText.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private async Task InstallUpdateAsync()
    {
        if (_updates == null || _handoff == null || _updateLifetime is not AppLifetime lifetime || _updateSnapshot?.OfferId is not Guid id) return;
        if (!ConfirmUpdateInstall()) return;
        var prepared = await _updates.PrepareApplyAsync(this, id, _updateWindowClosing.Token).ConfigureAwait(false);
        if (prepared == null) return;
        try
        {
            if (_updateHandoffOwned) await _handoff.RevokeAsync().ConfigureAwait(false);
            lock (_admissionLock) _updateHandoffOwned = true;
            await _handoff.PrepareAsync(prepared.Candidate, _updateWindowClosing.Token).ConfigureAwait(false);
            // The receipt is consumed exactly once by the lifetime owner. The
            // scope cannot disappear between accepting transfer and setting its
            // flag: both this check and StopAcceptance use the admission lock.
            lock (_admissionLock)
            {
                _updateWindowClosing.Token.ThrowIfCancellationRequested();
                if (!lifetime.TryRequestUpdateShutdown(this, prepared))
                    throw new InvalidOperationException("The prepared update could not be transferred.");
                _updateHandoffTransferred = true;
                _updateHandoffOwned = false;
            }
        }
        catch (OperationCanceledException) when (_updateWindowClosing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            try { await _handoff.RevokeAsync().ConfigureAwait(false); lock (_admissionLock) _updateHandoffOwned = false; }
            catch (Exception cleanup) { ex = new AggregateException(ex, cleanup); }
            var code = ex switch
            {
                IrisException ie => ie.Code,
                TimeoutException => ErrorCode.Timeout,
                _ => ErrorCode.Unknown
            };
            _updates.RollbackApply(prepared, code);
            LogCommandUnexpectedFailed(_logger, "InstallUpdate", ex.GetType().Name);
            return;
        }
    }
    internal void ReportUpdateShutdownFailure(UpdateSnapshot snapshot)
    {
        if (_disposed) return;
        StopAcceptance();
        lock (_admissionLock)
        {
            _updateHandoffTransferred = false;
            _updateSnapshot = snapshot;
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
        try
        {
            if (_handoff != null && _updateHandoffOwned)
            {
                await _handoff.RevokeAsync().ConfigureAwait(false);
                lock (_admissionLock) _updateHandoffOwned = false;
            }
        }
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
