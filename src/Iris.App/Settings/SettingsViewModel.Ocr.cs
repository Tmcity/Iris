using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed class OcrProviderChoice(string? id, string name) : INotifyPropertyChanged
{
    public string? Id { get; } = id;
    public string Name { get; private set; } = name;
    public void Rename(string value) { Name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed partial class SettingsViewModel
{
    private readonly ObservableCollection<OcrProfileDraftViewModel> _ocrProfiles = [];
    private readonly List<OcrProviderProfile> _deletedOcrProfiles = [];
    // Separate bounded queues are the domain key: a translation ID can never replace
    // or cancel the OCR operation with the same ID.
    private readonly SettingsDraftOperationQueue _ocrOperationQueue = new();
    private readonly HashSet<Task> _ocrRetryWork = [];
    private OcrProfileDraftViewModel? _selectedOcrProfile;
    private OcrTestOwner? _ocrTest;
    private long _ocrRequestSeq;
    private bool _mergingOcr;
    public OcrSettingsDraftViewModel Ocr { get; private set; } = null!;
    public ScreenshotSettingsDraftViewModel Screenshot { get; private set; } = null!;
    public ReadOnlyObservableCollection<OcrProfileDraftViewModel> OcrProfiles { get; private set; } = null!;
    public ObservableCollection<OcrProviderChoice> OcrActiveChoices { get; } = [];
    public ObservableCollection<OcrProviderChoice> OcrFallbackChoices { get; } = [new(null, UiText.OcrNoFallback)];
    public ICommand AddBaiduOcrCommand { get; private set; } = null!;
    public ICommand AddWindowsOcrCommand { get; private set; } = null!;
    public ICommand DeleteOcrProfileCommand { get; private set; } = null!;
    public ICommand TestOcrCommand { get; private set; } = null!;
    public ICommand CancelOcrTestCommand { get; private set; } = null!;
    public ICommand RetryOcrCleanupCommand { get; private set; } = null!;
    public string? OcrTestStatus { get; private set; }
    public bool HasOcrTestStatus => !string.IsNullOrEmpty(OcrTestStatus);
    private bool HasOcrRetryWork { get { lock (_admissionLock) return _ocrRetryWork.Count > 0; } }
    public bool HasOcrTestOwnership => _ocrTest != null || _ocrOperationQueue.HasActiveWork || HasOcrRetryWork;
    public bool HasFailedOcrCleanup => _ocrTest?.Failed == true;
    private bool OcrIsDirty => Ocr?.IsDirty == true || Screenshot?.IsDirty == true || _deletedOcrProfiles.Count > 0 || _ocrProfiles.Any(p => p.IsNew || p.IsDirty);
    private bool CanTestOcr => CanEdit && _editCoordinator != null && SelectedOcrProfile != null && !HasFailedOcrCleanup
        && !(_ocrTest is { } test && test.Target == SelectedOcrProfile && test.Revision == SelectedOcrProfile.Revision);
    public OcrProfileDraftViewModel? SelectedOcrProfile
    {
        get => _selectedOcrProfile;
        set { if (CanEdit && (value == null || _ocrProfiles.Contains(value))) SelectOcrProfile(value); }
    }

    private void InitializeOcr()
    {
        OcrProfiles = new(_ocrProfiles);
        Ocr = new(_settingsService.Current.Ocr, () => CanEdit && !_mergingOcr, OcrFieldsChanged);
        Screenshot = new(_settingsService.Current.Screenshot, () => CanEdit, OcrFieldsChanged);
        AddBaiduOcrCommand = new RelayCommand(() => AddOcrProfile(OcrProviderKind.BaiduCloud), () => CanEdit);
        AddWindowsOcrCommand = new RelayCommand(() => AddOcrProfile(OcrProviderKind.WindowsLocal), () => CanEdit);
        DeleteOcrProfileCommand = new RelayCommand(DeleteOcrProfile, () => CanEdit && SelectedOcrProfile is { IsBuiltIn: false });
        TestOcrCommand = new AsyncRelayCommand(ExecuteOcrTestAsync, () => CanTestOcr, ex => HandleCommandException("OcrTest", ex));
        CancelOcrTestCommand = new RelayCommand(CancelOcrTest, () => !_disposed && _ocrTest != null && !HasFailedOcrCleanup);
        RetryOcrCleanupCommand = new AsyncRelayCommand(RetryOcrCleanupAsync,
            () => !_disposed && !IsSaving && HasFailedOcrCleanup, ex => HandleCommandException("OcrCleanup", ex));
        MergeOcrSettings(reset: true);
    }

    private void OcrFieldsChanged()
    {
        OnPropertyChanged(nameof(IsDirty)); RefreshOcrChoices(); RaiseAllCommandsCanExecute();
    }
    private void OnOcrProfileEdited(OcrProfileDraftViewModel profile)
    {
        if (_ocrTest?.Target == profile) { Interlocked.Increment(ref _ocrRequestSeq); _ocrOperationQueue.CancelInFlight(); }
        OnPropertyChanged(nameof(SelectedOcrProfile)); OcrFieldsChanged();
    }
    private void SelectOcrProfile(OcrProfileDraftViewModel? value)
    {
        if (ReferenceEquals(_selectedOcrProfile, value)) return;
        _selectedOcrProfile?.InvalidateTest(); _selectedOcrProfile = value;
        Interlocked.Increment(ref _ocrRequestSeq); _ocrOperationQueue.CancelInFlight();
        OnPropertyChanged(nameof(SelectedOcrProfile)); RaiseOcrCommands();
    }
    public void AddOcrProfile(OcrProviderKind kind)
    {
        if (!CanEdit || !Enum.IsDefined(kind)) return;
        var profile = new OcrProviderProfile { Id = "ocr-" + Guid.NewGuid().ToString("N"), Kind = kind,
            DisplayName = kind == OcrProviderKind.BaiduCloud ? UiText.OcrBaidu : UiText.OcrWindows,
            BaiduOptions = kind == OcrProviderKind.BaiduCloud ? new() : null,
            WindowsOptions = kind == OcrProviderKind.WindowsLocal ? new() : null };
        var draft = new OcrProfileDraftViewModel(profile, true, () => CanEdit, OnOcrProfileEdited);
        _ocrProfiles.Add(draft); SelectOcrProfile(draft); OcrFieldsChanged();
    }
    public void DeleteOcrProfile()
    {
        if (!CanEdit || SelectedOcrProfile is not { IsBuiltIn: false } draft) return;
        if (draft.BaseProfile is { } baseline) _deletedOcrProfiles.Add(baseline);
        draft.InvalidateTest(); draft.Detach(); _ocrProfiles.Remove(draft);
        SelectOcrProfile(_ocrProfiles.FirstOrDefault()); OcrFieldsChanged();
    }
    private void RefreshOcrChoices()
    {
        if (Ocr == null) return;
        _mergingOcr = true;
        try
        {
            var ids = _ocrProfiles.Select(p => p.Id).Append(Ocr.ActiveProviderId.Value).Append(Ocr.FallbackProviderId.Value)
                .Where(id => id != null).Distinct(StringComparer.Ordinal).ToArray();
            foreach (string id in ids!)
            {
                var profile = _ocrProfiles.FirstOrDefault(p => p.Id == id);
                string label = profile == null ? UiText.OcrMissingSelection : string.IsNullOrWhiteSpace(profile.DisplayName.Value) ? UiText.OcrUnnamed : profile.DisplayName.Value;
                var choice = OcrActiveChoices.FirstOrDefault(c => c.Id == id);
                if (choice == null) { choice = new(id, label); OcrActiveChoices.Add(choice); OcrFallbackChoices.Add(choice); }
                else choice.Rename(label);
            }
            foreach (var old in OcrActiveChoices.Where(c => !ids.Contains(c.Id)).ToArray()) { OcrActiveChoices.Remove(old); OcrFallbackChoices.Remove(old); }
        }
        finally { _mergingOcr = false; }
    }
    private void MergeOcrSettings(bool reset)
    {
        if (Ocr == null) return;
        _mergingOcr = true;
        try
        {
            var current = _settingsService.Current;
            if (reset)
            {
                foreach (var p in _ocrProfiles) p.Detach();
                _ocrProfiles.Clear(); _deletedOcrProfiles.Clear();
            }
            foreach (var local in _ocrProfiles.ToArray())
            {
                var latest = current.Ocr.Providers.FirstOrDefault(p => p.Id == local.Id);
                if (latest != local.BaseProfile)
                {
                    local.InvalidateTest();
                    if (_ocrTest?.Target == local) { Interlocked.Increment(ref _ocrRequestSeq); _ocrOperationQueue.CancelInFlight(); }
                }
                if (latest == null && !local.IsDirty && !local.IsNew) { local.Detach(); _ocrProfiles.Remove(local); }
                else if (latest != null && latest != local.BaseProfile) local.Merge(latest);
            }
            foreach (var p in current.Ocr.Providers)
                if (!_ocrProfiles.Any(d => d.Id == p.Id) && !_deletedOcrProfiles.Any(d => d.Id == p.Id))
                    _ocrProfiles.Add(new(p, false, () => CanEdit, OnOcrProfileEdited));
            // Preserve existing configuration order; unsaved additions remain at the end.
            var ordered = _ocrProfiles.OrderBy(p => { int i = current.Ocr.Providers.ToList().FindIndex(c => c.Id == p.Id); return i < 0 ? int.MaxValue : i; }).ToArray();
            for (int i = 0; i < ordered.Length; i++) _ocrProfiles.Move(_ocrProfiles.IndexOf(ordered[i]), i);
            Ocr.Merge(current.Ocr, reset); Screenshot.Merge(current.Screenshot, reset);
            if (_selectedOcrProfile == null || !_ocrProfiles.Contains(_selectedOcrProfile)) SelectOcrProfile(_ocrProfiles.FirstOrDefault());
        }
        finally { _mergingOcr = false; }
        RefreshOcrChoices(); OnPropertyChanged(nameof(SelectedOcrProfile));
    }
    private bool TryCaptureOcr(out OcrSettingsPatch? ocr, out IReadOnlyList<OcrProfileChange> changes, out ScreenshotSettingsPatch? screenshot)
    {
        ocr = null; screenshot = null; changes = [];
        foreach (var p in _ocrProfiles.Where(p => p.IsNew || p.IsDirty))
            if (!p.Validate()) { StatusMessage = p.ErrorMessage; return false; }
        bool profilesDirty = _deletedOcrProfiles.Count > 0 || _ocrProfiles.Any(p => p.IsNew || p.IsDirty);
        if (!Ocr.TryCapture(OcrProfiles, profilesDirty, out ocr)) { StatusMessage = Ocr.ErrorMessage; return false; }
        if (!Screenshot.TryCapture(out screenshot)) { StatusMessage = Screenshot.ErrorMessage; return false; }
        changes = _deletedOcrProfiles.Select(p => new OcrProfileChange(OcrProfileChangeKind.Delete, p, null, new(CredentialEditKind.Keep)))
            .Concat(_ocrProfiles.Where(p => p.IsNew || p.IsDirty).Select(p => new OcrProfileChange(p.IsNew ? OcrProfileChangeKind.Add : OcrProfileChangeKind.Update,
                p.BaseProfile, p.BuildTarget(), p.BuildCredentials()))).ToArray();
        return true;
    }
    private void RaiseOcrCommands()
    {
        (AddBaiduOcrCommand as RelayCommand)?.RaiseCanExecuteChanged(); (AddWindowsOcrCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteOcrProfileCommand as RelayCommand)?.RaiseCanExecuteChanged(); (TestOcrCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelOcrTestCommand as RelayCommand)?.RaiseCanExecuteChanged(); (RetryOcrCleanupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasFailedOcrCleanup)); OnPropertyChanged(nameof(HasOcrTestOwnership)); OnPropertyChanged(nameof(OcrTestStatus));
        OnPropertyChanged(nameof(HasOcrTestStatus));
    }

    private sealed class OcrTestOwner(OcrProfileDraftViewModel target, long sequence, IOcrDraftTestSession session)
    {
        internal readonly OcrProfileDraftViewModel Target = target;
        internal readonly long Revision = target.Revision, Sequence = sequence;
        internal IOcrDraftTestSession? Session = session;
        internal ProviderTestResult? Result;
        internal Task<bool>? Cleanup;
        internal bool Failed, Canceled;
    }
    private bool OcrResultIsCurrent(OcrTestOwner owner) => !_disposed && !_isStopped && SelectedOcrProfile == owner.Target
        && owner.Target.Revision == owner.Revision && _ocrRequestSeq == owner.Sequence && _ocrProfiles.Contains(owner.Target);

    public async Task ExecuteOcrTestAsync()
    {
        if (!CanTestOcr || HasOcrRetryWork) return;
        BeforeSave?.Invoke();
        var target = SelectedOcrProfile!;
        if (!target.Validate()) return;
        var draft = new OcrProviderDraft(target.BuildTarget(), target.BaseProfile, target.BuildCredentials());
        long revision = target.Revision, sequence = Interlocked.Increment(ref _ocrRequestSeq);
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_admissionLock) _activeWork.Add(work.Task);
        try
        {
            await _ocrOperationQueue.EnqueueAsync(async token =>
            {
                OcrTestOwner? owner = null;
                try
                {
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        if (!CanEdit || token.IsCancellationRequested || _ocrTest != null || target != SelectedOcrProfile
                            || revision != target.Revision || sequence != _ocrRequestSeq) return Task.CompletedTask;
                        owner = new(target, sequence, _editCoordinator!.CreateOcrDraftTest(draft)); _ocrTest = owner;
                        target.SetTestMessage(null); OcrTestStatus = UiText.OcrTesting; RaiseOcrCommands();
                        return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (owner == null) return;
                    try { owner.Result = await owner.Session!.RunAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { owner.Canceled = true; }
                    catch (Exception ex)
                    {
                        LogTestDraftException(_logger, ex.GetType().Name);
                        owner.Result = new(false, TimeSpan.Zero, Error: new(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown)));
                    }
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        OcrTestStatus = UiText.OcrEnding;
                        if (OcrResultIsCurrent(owner) && owner.Result is { Success: false, Error: { } error }) owner.Target.SetTestMessage(ErrorMessages.GetDefault(error.Code));
                        RaiseOcrCommands(); return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    if (owner != null) await CleanupOcrOwnerAsync(owner).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await _uiDispatcher.InvokeAsync(() => { HandleCommandException("OcrTest", ex); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false); }
        finally { lock (_admissionLock) _activeWork.Remove(work.Task); work.TrySetResult(); }
    }
    private Task<bool> CleanupOcrOwnerAsync(OcrTestOwner owner)
    {
        lock (_admissionLock)
        {
            if (owner.Session == null) return Task.FromResult(true);
            if (owner.Cleanup is { IsCompleted: false }) return owner.Cleanup;
            return owner.Cleanup = CleanupCoreAsync();
        }
        async Task<bool> CleanupCoreAsync()
        {
            try { await owner.Session!.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                LogTestDraftException(_logger, ex.GetType().Name);
                owner.Failed = true;
                await _uiDispatcher.InvokeAsync(() => { OcrTestStatus = UiText.OcrEndFailed; RaiseOcrCommands(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            lock (_admissionLock)
            {
                owner.Session = null; owner.Failed = false;
                if (ReferenceEquals(_ocrTest, owner)) _ocrTest = null;
            }
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (OcrResultIsCurrent(owner)) owner.Target.SetTestMessage(owner.Result is { Success: true } ? UiText.OcrTestPassed
                    : owner.Result?.Error is { } error ? ErrorMessages.GetDefault(error.Code) : UiText.OcrTestCanceled);
                OcrTestStatus = null; RaiseOcrCommands(); return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
    }
    public void CancelOcrTest()
    {
        Interlocked.Increment(ref _ocrRequestSeq); _ocrOperationQueue.CancelInFlight();
        if (_ocrTest != null) { OcrTestStatus = UiText.OcrEnding; RaiseOcrCommands(); }
    }
    public async Task RetryOcrCleanupAsync()
    {
        OcrTestOwner owner;
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_admissionLock)
        {
            if (_disposed || IsSaving || _ocrTest is not { Failed: true } pending) return;
            owner = pending;
            _activeWork.Add(work.Task); _ocrRetryWork.Add(work.Task);
        }
        try
        {
            if (await CleanupOcrOwnerAsync(owner).ConfigureAwait(false) && _isStopped)
                await _uiDispatcher.InvokeAsync(() => { RequestClose?.Invoke(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_admissionLock) { _activeWork.Remove(work.Task); _ocrRetryWork.Remove(work.Task); }
            work.TrySetResult();
        }
    }
    private async Task<bool> DrainOcrTestsAsync(bool closing)
    {
        OcrTestOwner? failedAtEntry;
        Task[] retries;
        lock (_admissionLock)
        {
            failedAtEntry = _ocrTest is { Failed: true } failed ? failed : null;
            retries = _ocrRetryWork.ToArray();
        }
        if (closing) await _ocrOperationQueue.DrainAndCancelAllAsync().ConfigureAwait(false);
        else await _ocrOperationQueue.DrainAndCancelInFlightAsync().ConfigureAwait(false);
        // The provider may already be disposed while the original retry still owns
        // a queued UI publication. Never include the saving/reloading task itself.
        await Task.WhenAll(retries).ConfigureAwait(false);
        if (closing && retries.Length == 0 && failedAtEntry != null) await CleanupOcrOwnerAsync(failedAtEntry).ConfigureAwait(false);
        return _ocrTest == null;
    }
    internal void ShowCloseFailure()
    {
        StatusMessage = UiText.SettingsCloseFailed;
        RaiseAllCommandsCanExecute();
    }
    internal void LogCloseFailure(Exception error) => LogCommandUnexpectedFailed(_logger, "Close", error.GetType().Name);
    private void DisposeOcrDrafts()
    {
        if (HasOcrTestOwnership) throw new InvalidOperationException("OCR 测试尚未完成清理。");
        foreach (var p in _ocrProfiles) p.Detach();
        _ocrOperationQueue.Dispose(); _deletedOcrProfiles.Clear();
    }

    private async Task ReloadAfterOcrCleanupAsync()
    {
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_admissionLock) { _activeWork.Add(work.Task); IsSaving = true; FreezeAllDrafts(); }
        try
        {
            if (!await DrainOcrTestsAsync(closing: false).ConfigureAwait(false)) return;
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || _isStopped) return Task.CompletedTask;
                SyncBaselineFromCurrent(); _themeService.ClearPreview(_sessionOwner);
                StatusMessage = null; IsConflict = false; OnPropertyChanged(nameof(IsDirty));
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { await _uiDispatcher.InvokeAsync(() => { HandleCommandException("OcrReload", ex); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            try { await _uiDispatcher.InvokeAsync(CompleteSaveOrRetryOperationAsync, CancellationToken.None).ConfigureAwait(false); }
            finally { lock (_admissionLock) _activeWork.Remove(work.Task); work.TrySetResult(); }
        }
    }
}
