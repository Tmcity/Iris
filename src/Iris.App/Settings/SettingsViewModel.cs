using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.App.Settings;

internal sealed record PromptOptionItem(string? Id, string DisplayName);

internal sealed partial class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<AppTheme> _availableThemes = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    private static readonly IReadOnlyList<string> _availableLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];

    private readonly ISettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly GeneralSettingsSaveCoordinator _saveCoordinator;
    private readonly ISettingsEditCoordinator? _editCoordinator;
    private readonly ISettingsApplyReceiptManager? _receiptManager;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly object _sessionOwner = new();

    private readonly SettingsDraftOperationQueue _operationQueue = new();
    private readonly Lock _admissionLock = new();
    private readonly HashSet<Task> _activeWork = [];
    private bool _isStopped;
    private long _queryGeneration;
    private long _requestSeq;

    // 常规设置草稿
    private AppTheme _baseTheme;
    private AppTheme _targetTheme;
    private bool _themeDirty;

    private bool _baseLaunchAtStartup;
    private bool _targetLaunchAtStartup;
    private bool _launchAtStartupDirty;

    private bool _baseShowStartupNotification;
    private bool _targetShowStartupNotification;
    private bool _showStartupNotificationDirty;

    private string _baseLogLevel;
    private string _targetLogLevel;
    private bool _logLevelDirty;

    // 翻译服务草稿
    private string[] _baselineProfileIds = [];
    private readonly List<TranslationProfileDraftViewModel> _deletedProfiles = [];
    private TranslationProfileDraftViewModel? _selectedProfile;

    private string _currentPageKey = "general";
    private bool _isSaving;
    private string? _statusMessage;
    private bool _isConflict;
    private bool _canRetryAutoStart;
    private bool _canRetryPendingApply;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RequestClose;

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        IUiDispatcher uiDispatcher)
        : this(settingsService, themeService, saveCoordinator, null, null, uiDispatcher, null)
    {
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        IUiDispatcher uiDispatcher,
        ILogger<SettingsViewModel>? logger)
        : this(settingsService, themeService, saveCoordinator, null, null, uiDispatcher, logger)
    {
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        ISettingsEditCoordinator? editCoordinator,
        ISettingsApplyReceiptManager? receiptManager,
        IUiDispatcher uiDispatcher,
        ILogger<SettingsViewModel>? logger = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _saveCoordinator = saveCoordinator ?? throw new ArgumentNullException(nameof(saveCoordinator));
        _editCoordinator = editCoordinator;
        _receiptManager = receiptManager;
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger ?? NullLogger<SettingsViewModel>.Instance;

        // 从当前设置初始化常规基线与目标
        var general = _settingsService.Current.General;
        _baseTheme = general.Theme;
        _targetTheme = general.Theme;

        _baseLaunchAtStartup = general.LaunchAtStartup;
        _targetLaunchAtStartup = general.LaunchAtStartup;

        _baseShowStartupNotification = general.ShowStartupNotification;
        _targetShowStartupNotification = general.ShowStartupNotification;

        _baseLogLevel = general.LogLevel;
        _targetLogLevel = general.LogLevel;

        _canRetryAutoStart = false;

        // 初始化翻译服务列表
        Profiles = [];
        LoadProfilesFromCurrent();
        RefreshAvailablePrompts();

        if (_receiptManager != null)
        {
            _canRetryPendingApply = _receiptManager.HasPendingReceipts;
            _receiptManager.Changed += OnReceiptManagerChanged;
        }

        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync, () => CanEdit, ex => HandleCommandException("Save", ex));
        CancelCommand = new RelayCommand(ExecuteCancel, () => CanEdit);
        ReloadCommand = new RelayCommand(ExecuteReload, () => CanEdit);
        RetryAutoStartCommand = new AsyncRelayCommand(ExecuteRetryAutoStartAsync, () => !IsSaving && CanRetryAutoStart && !_disposed && !_isStopped, ex => HandleCommandException("RetryAutoStart", ex));
        RetryPendingApplyCommand = new AsyncRelayCommand(ExecuteRetryPendingApplyAsync, () => !IsSaving && CanRetryPendingApply && !_disposed && !_isStopped, ex => HandleCommandException("RetryPendingApply", ex));

        AddProfileCommand = new RelayCommand(p =>
        {
            if (p is TranslationProviderPreset preset)
            {
                AddProfileFromPreset(preset);
            }
        }, _ => CanEdit);
        DeleteProfileCommand = new RelayCommand(ExecuteDeleteProfile, () => CanEdit && SelectedProfile != null);
        MoveUpCommand = new RelayCommand(ExecuteMoveUp, () => CanEdit && CanMoveUp);
        MoveDownCommand = new RelayCommand(ExecuteMoveDown, () => CanEdit && CanMoveDown);

        TestDraftCommand = new AsyncRelayCommand(ExecuteTestDraftAsync, () => CanEdit && SelectedProfile != null, ex => HandleCommandException("TestDraft", ex));
        ListModelsCommand = new AsyncRelayCommand(ExecuteListModelsAsync, () => CanEdit && SelectedProfile != null, ex => HandleCommandException("ListModels", ex));

        _settingsService.Changed += OnExternalSettingsChanged;

        _ = QueryAutoStartStatusAsync();
    }

    public static IReadOnlyList<AppTheme> AvailableThemes => _availableThemes;
    public static IReadOnlyList<string> AvailableLogLevels => _availableLogLevels;
    public static IReadOnlyList<TranslationProviderPreset> AvailablePresets => TranslationProviderPresets.All;

    public ObservableCollection<TranslationProfileDraftViewModel> Profiles { get; }
    public ObservableCollection<PromptOptionItem> AvailablePrompts { get; } = [];

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RetryAutoStartCommand { get; }
    public ICommand RetryPendingApplyCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand TestDraftCommand { get; }
    public ICommand ListModelsCommand { get; }

    public TranslationProfileDraftViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!ReferenceEquals(_selectedProfile, value))
            {
                _selectedProfile = value;
                OnPropertyChanged();
                RaiseProfileCommandsCanExecute();
            }
        }
    }

    public bool CanMoveUp => SelectedProfile != null && Profiles.IndexOf(SelectedProfile) > 0;
    public bool CanMoveDown => SelectedProfile != null && Profiles.IndexOf(SelectedProfile) >= 0 && Profiles.IndexOf(SelectedProfile) < Profiles.Count - 1;

    public string CurrentPageKey
    {
        get => _currentPageKey;
        set
        {
            var normalized = SettingsWindowPresenter.NormalizePageKey(value);
            if (_currentPageKey != normalized)
            {
                _currentPageKey = normalized;
                OnPropertyChanged();
            }
        }
    }

    public bool CanEdit => !IsSaving && !_disposed && !_isStopped;

    public AppTheme Theme
    {
        get => _targetTheme;
        set
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }

            if (_targetTheme != value)
            {
                _targetTheme = value;
                _themeDirty = _targetTheme != _baseTheme;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
                if (_targetTheme == _baseTheme)
                {
                    _themeService.ClearPreview(_sessionOwner);
                }
                else
                {
                    _themeService.SetPreview(_sessionOwner, _targetTheme);
                }
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _targetLaunchAtStartup;
        set
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }

            if (_targetLaunchAtStartup != value)
            {
                _targetLaunchAtStartup = value;
                _launchAtStartupDirty = _targetLaunchAtStartup != _baseLaunchAtStartup;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool ShowStartupNotification
    {
        get => _targetShowStartupNotification;
        set
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }

            if (_targetShowStartupNotification != value)
            {
                _targetShowStartupNotification = value;
                _showStartupNotificationDirty = _targetShowStartupNotification != _baseShowStartupNotification;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string LogLevel
    {
        get => _targetLogLevel;
        set
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }

            if (!string.Equals(_targetLogLevel, value, StringComparison.OrdinalIgnoreCase))
            {
                _targetLogLevel = value;
                _logLevelDirty = !string.Equals(_targetLogLevel, _baseLogLevel, StringComparison.OrdinalIgnoreCase);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool IsOrderDirty
    {
        get
        {
            if (_deletedProfiles.Count > 0)
            {
                return true;
            }

            var currentIds = Profiles.Select(p => p.Id).ToArray();
            return !currentIds.SequenceEqual(_baselineProfileIds);
        }
    }

    public bool IsDirty
    {
        get
        {
            if (_themeDirty || _launchAtStartupDirty || _showStartupNotificationDirty || _logLevelDirty)
            {
                return true;
            }

            if (_deletedProfiles.Count > 0 || IsOrderDirty)
            {
                return true;
            }

            return Profiles.Any(p => p.IsDirty);
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (_isSaving != value)
            {
                _isSaving = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEdit));
                RaiseAllCommandsCanExecute();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsConflict
    {
        get => _isConflict;
        private set
        {
            if (_isConflict != value)
            {
                _isConflict = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanRetryAutoStart
    {
        get => _canRetryAutoStart;
        private set
        {
            if (_canRetryAutoStart != value)
            {
                _canRetryAutoStart = value;
                OnPropertyChanged();
                (RetryAutoStartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanRetryPendingApply
    {
        get => _canRetryPendingApply;
        private set
        {
            if (_canRetryPendingApply != value)
            {
                _canRetryPendingApply = value;
                OnPropertyChanged();
                (RetryPendingApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    internal object SessionOwner => _sessionOwner;

    private void LoadProfilesFromCurrent()
    {
        foreach (var p in Profiles)
        {
            p.PropertyChanged -= OnProfileDraftPropertyChanged;
        }
        Profiles.Clear();
        _deletedProfiles.Clear();

        var existingList = _settingsService.Current.Translation.Providers
            .OrderBy(p => p.Order)
            .ToList();

        _baselineProfileIds = existingList.Select(p => p.Id).ToArray();

        foreach (var p in existingList)
        {
            var draft = TranslationProfileDraftViewModel.FromExisting(p);
            draft.PropertyChanged += OnProfileDraftPropertyChanged;
            Profiles.Add(draft);
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void RefreshAvailablePrompts()
    {
        AvailablePrompts.Clear();
        AvailablePrompts.Add(new PromptOptionItem(null, UiText.SettingsProfilePromptDefault));
        AvailablePrompts.Add(new PromptOptionItem(BuiltInPrompts.DefaultTemplateId, BuiltInPrompts.Default.Name));

        foreach (var custom in _settingsService.Current.Translation.CustomPrompts)
        {
            AvailablePrompts.Add(new PromptOptionItem(custom.Id, custom.Name));
        }
    }

    private void OnProfileDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationProfileDraftViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public void AddProfileFromPreset(TranslationProviderPreset? preset)
    {
        if (!CanEdit || preset == null)
        {
            return;
        }

        var draft = TranslationProfileDraftViewModel.CreateFromPreset(preset);
        draft.PropertyChanged += OnProfileDraftPropertyChanged;
        Profiles.Add(draft);
        SelectedProfile = draft;

        OnPropertyChanged(nameof(IsDirty));
        RaiseProfileCommandsCanExecute();
    }

    public void ExecuteDeleteProfile()
    {
        if (!CanEdit || SelectedProfile == null)
        {
            return;
        }

        var target = SelectedProfile;
        target.ClearReplacementApiKey();
        target.PropertyChanged -= OnProfileDraftPropertyChanged;

        var idx = Profiles.IndexOf(target);
        Profiles.Remove(target);

        if (!target.IsNew)
        {
            _deletedProfiles.Add(target);
        }

        if (Profiles.Count > 0)
        {
            SelectedProfile = idx < Profiles.Count ? Profiles[idx] : Profiles[^1];
        }
        else
        {
            SelectedProfile = null;
        }

        OnPropertyChanged(nameof(IsDirty));
        RaiseProfileCommandsCanExecute();
    }

    public void ExecuteMoveUp()
    {
        if (!CanEdit || SelectedProfile == null)
        {
            return;
        }

        var idx = Profiles.IndexOf(SelectedProfile);
        if (idx > 0)
        {
            Profiles.Move(idx, idx - 1);
            OnPropertyChanged(nameof(IsDirty));
            RaiseProfileCommandsCanExecute();
        }
    }

    public void ExecuteMoveDown()
    {
        if (!CanEdit || SelectedProfile == null)
        {
            return;
        }

        var idx = Profiles.IndexOf(SelectedProfile);
        if (idx >= 0 && idx < Profiles.Count - 1)
        {
            Profiles.Move(idx, idx + 1);
            OnPropertyChanged(nameof(IsDirty));
            RaiseProfileCommandsCanExecute();
        }
    }

    public async Task ExecuteTestDraftAsync()
    {
        var target = SelectedProfile;
        if (!CanEdit || target == null)
        {
            return;
        }

        var valError = target.ValidateForTest();
        if (valError != null)
        {
            target.TestStatusMessage = valError;
            target.TestSuccess = false;
            return;
        }

        target.ClearTestResult();
        target.TestStatusMessage = UiText.SettingsTestingDraft;

        var draft = target.BuildDraftForTest();
        var sessionId = _sessionOwner;
        var profileId = target.Id;
        var rev = target.Revision;
        var reqSeq = Interlocked.Increment(ref _requestSeq);

        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (_disposed || _isStopped) return;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
        }

        try
        {
            await _operationQueue.EnqueueAsync(async ct =>
            {
                ProviderTestResult result;
                try
                {
                    if (_editCoordinator == null)
                    {
                        throw new InvalidOperationException("ISettingsEditCoordinator is null");
                    }
                    result = await _editCoordinator.TestDraftAsync(draft, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    result = new ProviderTestResult(false, TimeSpan.Zero, null, new ErrorInfo(ErrorCode.Unknown, ex.Message, null, null, false, null));
                }

                await _uiDispatcher.InvokeAsync(() =>
                {
                    lock (_admissionLock)
                    {
                        if (_disposed || _isStopped) return Task.CompletedTask;
                    }

                    if (!ReferenceEquals(_sessionOwner, sessionId)) return Task.CompletedTask;
                    if (SelectedProfile?.Id != profileId) return Task.CompletedTask;
                    if (target.Revision != rev || reqSeq != _requestSeq) return Task.CompletedTask;

                    target.TestSuccess = result.Success;
                    target.TestLatencyMs = (long)result.Latency.TotalMilliseconds;
                    if (result.Success)
                    {
                        target.TestStatusMessage = UiText.SettingsTestDraftSuccess;
                        target.TestSampleTranslation = result.Sample;
                    }
                    else
                    {
                        target.TestStatusMessage = result.Error?.Message ?? UiText.SettingsTestDraftFailed;
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_admissionLock)
            {
                _activeWork.Remove(workTcs.Task);
            }
            workTcs.TrySetResult();
        }
    }

    public async Task ExecuteListModelsAsync()
    {
        var target = SelectedProfile;
        if (!CanEdit || target == null)
        {
            return;
        }

        var valError = target.ValidateForListModels();
        if (valError != null)
        {
            target.ListModelsStatusMessage = valError;
            return;
        }

        target.ListModelsStatusMessage = UiText.SettingsListingModels;

        var draft = target.BuildDraftForListModels();
        var sessionId = _sessionOwner;
        var profileId = target.Id;
        var rev = target.Revision;
        var reqSeq = Interlocked.Increment(ref _requestSeq);

        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (_disposed || _isStopped) return;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
        }

        try
        {
            await _operationQueue.EnqueueAsync(async ct =>
            {
                IReadOnlyList<string> models;
                string? errorMsg = null;
                try
                {
                    if (_editCoordinator == null)
                    {
                        throw new InvalidOperationException("ISettingsEditCoordinator is null");
                    }
                    models = await _editCoordinator.ListDraftModelsAsync(draft, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IrisException ex)
                {
                    models = [];
                    errorMsg = ex.Message;
                }
                catch (Exception ex)
                {
                    models = [];
                    errorMsg = ex.GetType().Name;
                }

                await _uiDispatcher.InvokeAsync(() =>
                {
                    lock (_admissionLock)
                    {
                        if (_disposed || _isStopped) return Task.CompletedTask;
                    }

                    if (!ReferenceEquals(_sessionOwner, sessionId)) return Task.CompletedTask;
                    if (SelectedProfile?.Id != profileId) return Task.CompletedTask;
                    if (target.Revision != rev || reqSeq != _requestSeq) return Task.CompletedTask;

                    if (errorMsg == null)
                    {
                        target.AvailableModels.Clear();
                        foreach (var m in models)
                        {
                            target.AvailableModels.Add(m);
                        }
                        target.ListModelsStatusMessage = $"成功获取 {models.Count} 个模型";
                    }
                    else
                    {
                        target.ListModelsStatusMessage = $"获取模型列表失败: {errorMsg}";
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_admissionLock)
            {
                _activeWork.Remove(workTcs.Task);
            }
            workTcs.TrySetResult();
        }
    }

    private void RaiseProfileCommandsCanExecute()
    {
        (DeleteProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TestDraftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ListModelsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseAllCommandsCanExecute()
    {
        (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RetryAutoStartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryPendingApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RaiseProfileCommandsCanExecute();
    }

    internal void StopAcceptance()
    {
        lock (_admissionLock)
        {
            _isStopped = true;
            ++_queryGeneration;
        }
    }

    internal async Task WaitForActiveWorkAsync()
    {
        StopAcceptance();
        await _operationQueue.DrainAndCancelAllAsync().ConfigureAwait(false);

        while (true)
        {
            Task[] tasks;
            lock (_admissionLock)
            {
                if (_activeWork.Count == 0)
                {
                    break;
                }
                tasks = _activeWork.ToArray();
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private Task QueryAutoStartStatusAsync()
    {
        TaskCompletionSource workTcs;
        long gen;
        bool expectedStartup;
        lock (_admissionLock)
        {
            if (_disposed || _isStopped)
            {
                return Task.CompletedTask;
            }
            gen = ++_queryGeneration;
            expectedStartup = _settingsService.Current.General.LaunchAtStartup;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
        }

        return RunQueryAutoStartStatusAsync(gen, expectedStartup, workTcs);
    }

    private async Task RunQueryAutoStartStatusAsync(long gen, bool expectedStartup, TaskCompletionSource workTcs)
    {
        try
        {
            var isPending = await _saveCoordinator.QueryAutoStartPendingAsync(CancellationToken.None).ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_admissionLock)
                {
                    if (_disposed || _isStopped || gen != _queryGeneration)
                    {
                        return Task.CompletedTask;
                    }

                    if (_settingsService.Current.General.LaunchAtStartup != expectedStartup)
                    {
                        return Task.CompletedTask;
                    }

                    CanRetryAutoStart = isPending;
                    if (isPending && StatusMessage == UiText.SettingsStatusSaved)
                    {
                        StatusMessage = UiText.SettingsStatusSavedAutoStartStillPending;
                    }
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_admissionLock)
                {
                    if (!_disposed && !_isStopped && gen == _queryGeneration && _settingsService.Current.General.LaunchAtStartup == expectedStartup)
                    {
                        CanRetryAutoStart = true;
                    }
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_admissionLock)
            {
                _activeWork.Remove(workTcs.Task);
            }
            workTcs.TrySetResult();
        }
    }

    public async Task ExecuteSaveAsync()
    {
        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }
            ++_queryGeneration;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
            IsSaving = true;
        }

        StatusMessage = UiText.SettingsStatusSaving;

        try
        {
            // 保存前先取消并观察在途草稿测试/模型列表任务退出
            await _operationQueue.DrainAndCancelAllAsync().ConfigureAwait(false);

            lock (_admissionLock)
            {
                if (_disposed || _isStopped)
                {
                    return;
                }
            }

            // 保存前完整校验所有服务
            foreach (var profile in Profiles)
            {
                var valErr = profile.ValidateForSave();
                if (valErr != null)
                {
                    StatusMessage = $"{profile.DisplayName}: {valErr}";
                    return;
                }
            }

            // 组装 General 补丁
            var themeEdit = _themeDirty ? new SettingsFieldEdit<AppTheme>(_baseTheme, _targetTheme) : null;
            var launchEdit = _launchAtStartupDirty ? new SettingsFieldEdit<bool>(_baseLaunchAtStartup, _targetLaunchAtStartup) : null;
            var notifEdit = _showStartupNotificationDirty ? new SettingsFieldEdit<bool>(_baseShowStartupNotification, _targetShowStartupNotification) : null;
            var logEdit = _logLevelDirty ? new SettingsFieldEdit<string>(_baseLogLevel, _targetLogLevel) : null;
            var generalPatch = new GeneralSettingsPatch(themeEdit, launchEdit, notifEdit, logEdit);

            // 组装 Translation 变更
            var translationChanges = new List<TranslationProfileChange>();

            foreach (var deleted in _deletedProfiles)
            {
                translationChanges.Add(new TranslationProfileChange(
                    TranslationProfileChangeKind.Delete,
                    deleted.BaseProfile,
                    null,
                    new ProviderCredentialEdit(CredentialEditKind.Keep)));
            }

            for (int i = 0; i < Profiles.Count; i++)
            {
                var p = Profiles[i];
                if (p.IsNew)
                {
                    translationChanges.Add(new TranslationProfileChange(
                        TranslationProfileChangeKind.Add,
                        null,
                        p.BuildTargetProfile(i),
                        p.BuildCredentialEdit()));
                }
                else if (p.IsDirty)
                {
                    translationChanges.Add(new TranslationProfileChange(
                        TranslationProfileChangeKind.Update,
                        p.BaseProfile,
                        p.BuildTargetProfile(i),
                        p.BuildCredentialEdit()));
                }
            }

            // 组装顺序变更
            TranslationOrderChange? orderChange = null;
            if (IsOrderDirty)
            {
                orderChange = new TranslationOrderChange(
                    _baselineProfileIds.ToArray(),
                    Profiles.Select(p => p.Id).ToArray());
            }

            bool hasGeneralDirty = _themeDirty || _launchAtStartupDirty || _showStartupNotificationDirty || _logLevelDirty;
            bool hasTranslationDirty = translationChanges.Count > 0 || orderChange != null;

            if (!hasGeneralDirty && !hasTranslationDirty)
            {
                _themeService.ClearPreview(_sessionOwner);
                StatusMessage = UiText.SettingsStatusSaved;
                IsConflict = false;
                OnPropertyChanged(nameof(IsDirty));
                return;
            }

            bool wasLaunchAtStartupDirty = _launchAtStartupDirty;
            bool targetLaunchAtStartup = _targetLaunchAtStartup;

            var plan = new SettingsEditPlan(generalPatch, translationChanges, orderChange);
            var result = await _saveCoordinator.CommitPlanAsync(plan, wasLaunchAtStartupDirty, targetLaunchAtStartup, CancellationToken.None).ConfigureAwait(false);

            lock (_admissionLock)
            {
                if (_disposed || _isStopped)
                {
                    return;
                }
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_admissionLock)
                {
                    if (_disposed || _isStopped)
                    {
                        return Task.CompletedTask;
                    }
                }

                switch (result.Status)
                {
                    case GeneralSaveStatus.SavedAndApplied:
                        _themeService.ClearPreview(_sessionOwner);
                        SyncBaselineFromCurrent();
                        IsConflict = false;
                        if (wasLaunchAtStartupDirty)
                        {
                            var latestStartup = _settingsService.Current.General.LaunchAtStartup;
                            if (targetLaunchAtStartup == latestStartup)
                            {
                                CanRetryAutoStart = false;
                                StatusMessage = UiText.SettingsStatusSaved;
                            }
                            else
                            {
                                CanRetryAutoStart = true;
                                StatusMessage = UiText.SettingsStatusSavedAutoStartStillPending;
                            }
                        }
                        else
                        {
                            StatusMessage = CanRetryAutoStart ? UiText.SettingsStatusSavedAutoStartStillPending : UiText.SettingsStatusSaved;
                        }
                        OnPropertyChanged(nameof(IsDirty));
                        break;

                    case GeneralSaveStatus.SavedApplicationPending:
                        _themeService.ClearPreview(_sessionOwner);
                        SyncBaselineFromCurrent();
                        StatusMessage = result.FailureReason ?? UiText.SettingsStatusSavedPendingApply;
                        IsConflict = false;
                        if (_receiptManager?.HasPendingReceipts == true)
                        {
                            CanRetryPendingApply = true;
                        }
                        if (wasLaunchAtStartupDirty || _saveCoordinator.IsAutoStartPending())
                        {
                            CanRetryAutoStart = true;
                        }
                        OnPropertyChanged(nameof(IsDirty));
                        break;

                    case GeneralSaveStatus.SavedPendingApplyAndAutoStart:
                        _themeService.ClearPreview(_sessionOwner);
                        SyncBaselineFromCurrent();
                        StatusMessage = UiText.SettingsStatusSavedPendingApplyAndAutoStart;
                        IsConflict = false;
                        CanRetryPendingApply = true;
                        CanRetryAutoStart = true;
                        OnPropertyChanged(nameof(IsDirty));
                        break;

                    case GeneralSaveStatus.Uncommitted:
                    default:
                        StatusMessage = result.FailureReason ?? UiText.SettingsStatusSaveFailed;
                        IsConflict = result.IsConflict;
                        break;
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleCommandException("Save", ex);
        }
        finally
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    IsSaving = false;
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                IsSaving = false;
            }
            finally
            {
                lock (_admissionLock)
                {
                    _activeWork.Remove(workTcs.Task);
                }
                workTcs.TrySetResult();
            }
        }
    }

    private void SyncBaselineFromCurrent()
    {
        var current = _settingsService.Current.General;

        _themeDirty = false;
        _baseTheme = current.Theme;
        _targetTheme = current.Theme;
        OnPropertyChanged(nameof(Theme));

        _launchAtStartupDirty = false;
        _baseLaunchAtStartup = current.LaunchAtStartup;
        _targetLaunchAtStartup = current.LaunchAtStartup;
        OnPropertyChanged(nameof(LaunchAtStartup));

        _showStartupNotificationDirty = false;
        _baseShowStartupNotification = current.ShowStartupNotification;
        _targetShowStartupNotification = current.ShowStartupNotification;
        OnPropertyChanged(nameof(ShowStartupNotification));

        _logLevelDirty = false;
        _baseLogLevel = current.LogLevel;
        _targetLogLevel = current.LogLevel;
        OnPropertyChanged(nameof(LogLevel));

        // 重建 Translation 基线
        LoadProfilesFromCurrent();
        RefreshAvailablePrompts();
    }

    public void ExecuteCancel()
    {
        if (IsSaving || _disposed || _isStopped)
        {
            return;
        }

        foreach (var p in Profiles)
        {
            p.ClearReplacementApiKey();
        }
        _deletedProfiles.Clear();

        _themeService.ClearPreview(_sessionOwner);
        RequestClose?.Invoke();
    }

    public void ExecuteReload()
    {
        if (IsSaving || _disposed || _isStopped)
        {
            return;
        }

        SyncBaselineFromCurrent();
        _themeService.ClearPreview(_sessionOwner);

        StatusMessage = null;
        IsConflict = false;
        _ = QueryAutoStartStatusAsync();

        if (_receiptManager != null)
        {
            CanRetryPendingApply = _receiptManager.HasPendingReceipts;
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    public async Task ExecuteRetryAutoStartAsync()
    {
        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }
            ++_queryGeneration;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
            IsSaving = true;
        }

        StatusMessage = UiText.SettingsStatusRetryingAutoStart;

        var targetStartup = _settingsService.Current.General.LaunchAtStartup;

        try
        {
            var result = await _saveCoordinator.RetryAutoStartAsync();
            if (_disposed || _isStopped)
            {
                return;
            }

            if (result.Status == GeneralSaveStatus.SavedAndApplied)
            {
                var latestStartup = _settingsService.Current.General.LaunchAtStartup;
                if (targetStartup == latestStartup)
                {
                    StatusMessage = UiText.SettingsStatusAutoStartApplied;
                    CanRetryAutoStart = false;
                }
                else
                {
                    StatusMessage = UiText.SettingsStatusSavedAutoStartStillPending;
                    CanRetryAutoStart = true;
                }
            }
            else
            {
                StatusMessage = result.FailureReason ?? UiText.SettingsStatusAutoStartRetryFailed;
                CanRetryAutoStart = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleCommandException("RetryAutoStart", ex);
        }
        finally
        {
            try
            {
                IsSaving = false;
            }
            finally
            {
                lock (_admissionLock)
                {
                    _activeWork.Remove(workTcs.Task);
                }
                workTcs.TrySetResult();
            }
        }
    }

    public async Task ExecuteRetryPendingApplyAsync()
    {
        if (_receiptManager == null || _editCoordinator == null)
        {
            return;
        }

        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (IsSaving || _disposed || _isStopped)
            {
                return;
            }
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
            IsSaving = true;
        }

        StatusMessage = UiText.SettingsStatusRetryingPendingApply;

        try
        {
            var pendingSnapshot = _receiptManager.GetPendingReceipts();
            bool allComplete = true;
            string? lastError = null;

            foreach (var entry in pendingSnapshot)
            {
                var applyResult = await _editCoordinator.RetryApplyAsync(entry.Receipt, CancellationToken.None).ConfigureAwait(false);
                if (applyResult.IsComplete)
                {
                    _receiptManager.RemoveReceipt(entry.Receipt);
                }
                else
                {
                    allComplete = false;
                    lastError = applyResult.Error?.Message;
                    _receiptManager.UpdateReceiptError(entry.Receipt, lastError);
                }
            }

            if (_disposed || _isStopped)
            {
                return;
            }

            if (allComplete || !_receiptManager.HasPendingReceipts)
            {
                StatusMessage = UiText.SettingsStatusPendingApplyCompleted;
                CanRetryPendingApply = false;
            }
            else
            {
                StatusMessage = lastError ?? UiText.SettingsStatusPendingApplyFailed;
                CanRetryPendingApply = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleCommandException("RetryPendingApply", ex);
        }
        finally
        {
            try
            {
                IsSaving = false;
            }
            finally
            {
                lock (_admissionLock)
                {
                    _activeWork.Remove(workTcs.Task);
                }
                workTcs.TrySetResult();
            }
        }
    }

    private void OnReceiptManagerChanged(object? sender, EventArgs e)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            lock (_admissionLock)
            {
                if (_disposed || _isStopped) return Task.CompletedTask;
            }

            if (_receiptManager != null)
            {
                CanRetryPendingApply = _receiptManager.HasPendingReceipts;
            }
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private void OnExternalSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            lock (_admissionLock)
            {
                if (_disposed || _isStopped)
                {
                    return Task.CompletedTask;
                }
                ++_queryGeneration;
            }

            var currentGeneral = _settingsService.Current.General;

            // 未编辑常规字段跟随最新当前值；已编辑字段保留草稿与原基线以支持冲突检测
            if (!_themeDirty)
            {
                _baseTheme = currentGeneral.Theme;
                _targetTheme = currentGeneral.Theme;
                OnPropertyChanged(nameof(Theme));
                if (_themeService.PreviewOwner == _sessionOwner)
                {
                    _themeService.ClearPreview(_sessionOwner);
                }
            }

            if (!_launchAtStartupDirty)
            {
                _baseLaunchAtStartup = currentGeneral.LaunchAtStartup;
                _targetLaunchAtStartup = currentGeneral.LaunchAtStartup;
                OnPropertyChanged(nameof(LaunchAtStartup));
                _ = QueryAutoStartStatusAsync();
            }

            if (!_showStartupNotificationDirty)
            {
                _baseShowStartupNotification = currentGeneral.ShowStartupNotification;
                _targetShowStartupNotification = currentGeneral.ShowStartupNotification;
                OnPropertyChanged(nameof(ShowStartupNotification));
            }

            if (!_logLevelDirty)
            {
                _baseLogLevel = currentGeneral.LogLevel;
                _targetLogLevel = currentGeneral.LogLevel;
                OnPropertyChanged(nameof(LogLevel));
            }

            // 外部更新对翻译服务草稿的影响：
            // 未编辑项同步基线与值；已编辑项保留基线供 Core 冲突检测；外部删除不复活
            var currentProviders = _settingsService.Current.Translation.Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
            if (!IsOrderDirty)
            {
                _baselineProfileIds = _settingsService.Current.Translation.Providers.OrderBy(p => p.Order).Select(p => p.Id).ToArray();
            }

            foreach (var p in Profiles.ToList())
            {
                if (currentProviders.TryGetValue(p.Id, out var updated))
                {
                    if (!p.IsDirty)
                    {
                        p.SyncBaseline(updated);
                    }
                }
            }

            RefreshAvailablePrompts();
            OnPropertyChanged(nameof(IsDirty));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private void HandleCommandException(string operation, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return;
        }

        LogCommandUnexpectedFailed(_logger, operation, ex.GetType().Name);

        StatusMessage = operation switch
        {
            "RetryAutoStart" => UiText.SettingsStatusAutoStartRetryFailed,
            "RetryPendingApply" => UiText.SettingsStatusPendingApplyFailed,
            _ => UiText.SettingsStatusSaveFailed,
        };
        IsSaving = false;
        OnPropertyChanged(nameof(CanEdit));
    }

    public void Dispose()
    {
        lock (_admissionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isStopped = true;
            ++_queryGeneration;
        }

        _settingsService.Changed -= OnExternalSettingsChanged;
        if (_receiptManager != null)
        {
            _receiptManager.Changed -= OnReceiptManagerChanged;
        }

        foreach (var p in Profiles)
        {
            p.PropertyChanged -= OnProfileDraftPropertyChanged;
            p.ClearReplacementApiKey();
        }
        foreach (var p in _deletedProfiles)
        {
            p.ClearReplacementApiKey();
        }

        _operationQueue.Dispose();
        _themeService.ClearPreview(_sessionOwner);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (_disposed)
        {
            return;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [LoggerMessage(EventId = 1, Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "设置命令 {Operation} 发生意外异常：{ExceptionType}")]
    private static partial void LogCommandUnexpectedFailed(ILogger logger, string operation, string exceptionType);
}
