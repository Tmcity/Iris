using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.App.Settings;

internal sealed class PromptOptionItem(string? id, string displayName) : INotifyPropertyChanged
{
    public string? Id { get; } = id;
    private string _displayName = displayName;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed partial class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<AppTheme> _availableThemes = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    private static readonly IReadOnlyList<string> _availableLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];
    private static readonly CompositeFormat s_hotkeyDuplicateConflictFormat = CompositeFormat.Parse(UiText.SettingsHotkeyDuplicateConflict);
    private static readonly CompositeFormat s_hotkeyReservedConflictFormat = CompositeFormat.Parse(UiText.SettingsHotkeyReservedConflict);

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
    internal bool IsStopped => _isStopped;
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
    private bool _canRetryHotkeys;
    private bool _disposed;

    // 快捷键草稿与协调
    private readonly IHotkeyBindingPolicy _hotkeyBindingPolicy;
    private readonly HotkeyCoordinator? _hotkeyCoordinator;
    private readonly HotkeyRecordingCoordinator? _recordingCoordinator;
    private Func<nint>? _windowHandleProvider;
    private HotkeyItemDraftViewModel? _activeRecordingItem;
    private Task? _activeRecordingTask;
    private long _recordingSessionSeq;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RequestClose;

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        IUiDispatcher uiDispatcher)
        : this(settingsService, themeService, saveCoordinator, null, null, uiDispatcher, null, null, null, null)
    {
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        IUiDispatcher uiDispatcher,
        ILogger<SettingsViewModel>? logger)
        : this(settingsService, themeService, saveCoordinator, null, null, uiDispatcher, null, null, null, logger)
    {
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        ISettingsEditCoordinator? editCoordinator,
        ISettingsApplyReceiptManager? receiptManager,
        IUiDispatcher uiDispatcher)
        : this(settingsService, themeService, saveCoordinator, editCoordinator, receiptManager, uiDispatcher, null, null, null, null)
    {
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        ISettingsEditCoordinator? editCoordinator,
        ISettingsApplyReceiptManager? receiptManager,
        IUiDispatcher uiDispatcher,
        ILogger<SettingsViewModel>? logger)
        : this(settingsService, themeService, saveCoordinator, editCoordinator, receiptManager, uiDispatcher, null, null, null, logger)
    {
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        ISettingsEditCoordinator? editCoordinator,
        ISettingsApplyReceiptManager? receiptManager,
        IUiDispatcher uiDispatcher,
        IHotkeyBindingPolicy? hotkeyBindingPolicy,
        HotkeyCoordinator? hotkeyCoordinator = null,
        HotkeyRecordingCoordinator? recordingCoordinator = null,
        ILogger<SettingsViewModel>? logger = null,
        Iris.App.Selection.SelectionIconController? mouseController = null,
        Iris.Core.Network.IProxyConnectivityTester? networkTester = null,
        TimeProvider? networkTime = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _saveCoordinator = saveCoordinator ?? throw new ArgumentNullException(nameof(saveCoordinator));
        _editCoordinator = editCoordinator;
        _receiptManager = receiptManager;
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _hotkeyBindingPolicy = hotkeyBindingPolicy ?? new DefaultHotkeyBindingPolicy();
        _hotkeyCoordinator = hotkeyCoordinator;
        _recordingCoordinator = recordingCoordinator;
        if (_recordingCoordinator != null) _recordingCoordinator.AdmissionChanged += OnRecordingAdmissionChanged;
        _logger = logger ?? NullLogger<SettingsViewModel>.Instance;
        Selection = new SelectionSettingsDraftViewModel(_settingsService.Current.Selection, () => CanEdit, () =>
        {
            OnPropertyChanged(nameof(IsDirty));
            RaiseAllCommandsCanExecute();
        });

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
        Profiles = new ControlledProfileCollection(this);
        LoadProfilesFromCurrent();
        RefreshAvailablePrompts();

        // 初始化快捷键列表
        LoadHotkeysFromCurrent();

        if (_receiptManager != null)
        {
            _canRetryPendingApply = _receiptManager.HasPendingReceipts;
            _receiptManager.Changed += OnReceiptManagerChanged;
        }

        if (_hotkeyCoordinator != null)
        {
            _hotkeyCoordinator.RuntimeSnapshotChanged += OnHotkeyRuntimeSnapshotChanged;
        }

        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync, () => CanSave, ex => HandleCommandException("Save", ex));
        CancelCommand = new RelayCommand(ExecuteCancel, () => !IsSaving && !_disposed && !_isStopped);
        ReloadCommand = new RelayCommand(ExecuteReload, () => CanEdit);
        RetryAutoStartCommand = new AsyncRelayCommand(ExecuteRetryAutoStartAsync, () => !IsSaving && CanRetryAutoStart && !_disposed && !_isStopped, ex => HandleCommandException("RetryAutoStart", ex));
        RetryPendingApplyCommand = new AsyncRelayCommand(ExecuteRetryPendingApplyAsync, () => !IsSaving && CanRetryPendingApply && !_disposed && !_isStopped, ex => HandleCommandException("RetryPendingApply", ex));
        RetryHotkeysCommand = new AsyncRelayCommand(ExecuteRetryHotkeysAsync, () => !IsSaving && (CanRetryHotkeys || HasFailedRecordingCleanup) && !_disposed && (!_isStopped || HasFailedRecordingCleanup), ex => HandleCommandException("RetryHotkeys", ex));
        InitializeMouse(mouseController);

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

        History = new(_settingsService.Current.History, () => CanEdit, () => { OnPropertyChanged(nameof(IsDirty)); RaiseAllCommandsCanExecute(); });
        InitializeOcr();
        _networkTester = networkTester; _networkTime = networkTime ?? TimeProvider.System;
        InitializeNetwork();
        _settingsService.Changed += OnExternalSettingsChanged;

        _ = QueryAutoStartStatusAsync();
    }

    public static IReadOnlyList<AppTheme> AvailableThemes => _availableThemes;
    public static IReadOnlyList<string> AvailableLogLevels => _availableLogLevels;
    public static IReadOnlyList<TranslationProviderPreset> AvailablePresets => TranslationProviderPresets.All;

    public ControlledProfileCollection Profiles { get; }
    public ObservableCollection<PromptOptionItem> AvailablePrompts { get; } = [];
    public ObservableCollection<HotkeyItemDraftViewModel> Hotkeys { get; } = [];
    public SelectionSettingsDraftViewModel Selection { get; }
    public HistorySettingsDraftViewModel History { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RetryAutoStartCommand { get; }
    public ICommand RetryPendingApplyCommand { get; }
    public ICommand RetryHotkeysCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand TestDraftCommand { get; }
    public ICommand ListModelsCommand { get; }

    public bool CanRetryHotkeys
    {
        get => _canRetryHotkeys;
        private set
        {
            if (_canRetryHotkeys != value)
            {
                _canRetryHotkeys = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRetryHotkeysOrCleanup));
                (RetryHotkeysCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanRetryHotkeysOrCleanup => CanRetryHotkeys || HasFailedRecordingCleanup;
    public bool CanAccessHotkeysTab => !IsReadOnly && !IsSaving && !_disposed && !_isStopped;

    internal bool CanStartRecording => _recordingCoordinator?.CanStartRecording != false;
    private Task OnRecordingAdmissionChanged() => _uiDispatcher.InvokeAsync(() => { RaiseAllCommandsCanExecute(); return Task.CompletedTask; }, CancellationToken.None);
    public bool IsAnyRecording => _activeRecordingItem != null;

    internal Action? BeforeSave { get; set; }

    internal void SetSelectedProfileInternal(TranslationProfileDraftViewModel? value)
    {
        var old = _selectedProfile;
        if (!ReferenceEquals(old, value))
        {
            _selectedProfile = value;
            Interlocked.Increment(ref _requestSeq);
            _operationQueue.CancelInFlight();
            if (old != null)
            {
                if (old.TestStatusMessage == UiText.SettingsTestingDraft)
                {
                    old.ClearTestResult();
                }
                if (old.ListModelsStatusMessage == UiText.SettingsListingModels)
                {
                    old.ListModelsStatusMessage = null;
                }
            }
            OnPropertyChanged(nameof(SelectedProfile));
            RaiseProfileCommandsCanExecute();
        }
    }

    public TranslationProfileDraftViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (IsSaving) return;
            SetSelectedProfileInternal(value);
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
                if (IsAnyRecording)
                {
                    _ = CancelRecordingAsync();
                }
                _currentPageKey = normalized;
                OnPropertyChanged();
            }
        }
    }

    public bool HasFailedRecordingCleanup => _recordingCoordinator?.HasFailedCleanup == true;
    public bool IsReadOnly => _settingsService.IsReadOnly;
    public bool CanEdit => !IsReadOnly && !IsSaving && !_disposed && !_isStopped && !IsAnyRecording && !HasFailedRecordingCleanup;
    public bool CanSave => CanEdit && !HasFailedNetworkCleanup && !IsAnyRecording && !HasFailedRecordingCleanup;

    public AppTheme Theme
    {
        get => _targetTheme;
        set
        {
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
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
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
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
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
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
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
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

            if (Profiles.Any(p => p.IsDirty))
            {
                return true;
            }

            return Network.IsDirty || OcrIsDirty || History.IsDirty || Selection.IsDirty || Hotkeys.Any(h => h.IsDirty);
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
        using (Profiles.AllowInternalMutation())
        {
            var oldTargetId = _selectedProfile?.Id;

            foreach (var p in Profiles)
            {
                p.OnEdited = null;
                p.ClearReplacementApiKey();
                p.PropertyChanged -= OnProfileDraftPropertyChanged;
            }
            if (_selectedProfile != null && !Profiles.Contains(_selectedProfile))
            {
                _selectedProfile.OnEdited = null;
                _selectedProfile.ClearReplacementApiKey();
                _selectedProfile.PropertyChanged -= OnProfileDraftPropertyChanged;
            }

            Profiles.Clear();
            foreach (var d in _deletedProfiles)
            {
                d.ClearReplacementApiKey();
            }
            _deletedProfiles.Clear();

            var existingList = _settingsService.Current.Translation.Providers
                .OrderBy(p => p.Order)
                .ToList();

            _baselineProfileIds = existingList.Select(p => p.Id).ToArray();

            foreach (var p in existingList)
            {
                var draft = TranslationProfileDraftViewModel.FromExisting(p);
                draft.OnEdited = OnProfileEdited;
                draft.PropertyChanged += OnProfileDraftPropertyChanged;
                Profiles.Add(draft);
            }

            var nextSelected = oldTargetId != null
                ? Profiles.FirstOrDefault(p => p.Id == oldTargetId)
                : null;
            nextSelected ??= Profiles.FirstOrDefault();

            SetSelectedProfileInternal(nextSelected);
        }
    }

    private void OnProfileEdited(TranslationProfileDraftViewModel p)
    {
        _operationQueue.CancelInFlight();
        Interlocked.Increment(ref _requestSeq);
        p.ClearTestResult();
        p.ListModelsStatusMessage = null;
    }

    private void RefreshAvailablePrompts()
    {
        var desired = new List<PromptOptionItem>
        {
            new(null, UiText.SettingsProfilePromptDefault),
            new(BuiltInPrompts.DefaultTemplateId, BuiltInPrompts.Default.Name)
        };

        foreach (var custom in _settingsService.Current.Translation.CustomPrompts)
        {
            desired.Add(new PromptOptionItem(custom.Id, custom.Name));
        }

        // 保留草稿中已选中但已被外部删除的模板，以便 ComboBox 保持当前值并不悄悄置空
        var referencedIds = Profiles.Select(p => p.PromptTemplateId)
            .Where(id => id != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (SelectedProfile?.PromptTemplateId != null && !referencedIds.Contains(SelectedProfile.PromptTemplateId))
        {
            referencedIds.Add(SelectedProfile.PromptTemplateId);
        }

        foreach (var id in referencedIds)
        {
            if (!desired.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal)))
            {
                desired.Add(new PromptOptionItem(id, $"[已移除] {id}"));
            }
        }

        bool isIdentical = AvailablePrompts.Count == desired.Count;
        if (isIdentical)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (AvailablePrompts[i].Id != desired[i].Id ||
                    AvailablePrompts[i].DisplayName != desired[i].DisplayName)
                {
                    isIdentical = false;
                    break;
                }
            }
        }

        if (isIdentical)
        {
            return;
        }

        for (int i = AvailablePrompts.Count - 1; i >= 0; i--)
        {
            var cur = AvailablePrompts[i];
            if (!desired.Any(d => d.Id == cur.Id))
            {
                AvailablePrompts.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var target = desired[i];
            if (i < AvailablePrompts.Count)
            {
                if (AvailablePrompts[i].Id == target.Id)
                {
                    if (AvailablePrompts[i].DisplayName != target.DisplayName)
                    {
                        AvailablePrompts[i].DisplayName = target.DisplayName;
                    }
                }
                else
                {
                    var existingIdx = -1;
                    for (int j = i + 1; j < AvailablePrompts.Count; j++)
                    {
                        if (AvailablePrompts[j].Id == target.Id)
                        {
                            existingIdx = j;
                            break;
                        }
                    }

                    if (existingIdx >= 0)
                    {
                        AvailablePrompts.Move(existingIdx, i);
                        if (AvailablePrompts[i].DisplayName != target.DisplayName)
                        {
                            AvailablePrompts[i].DisplayName = target.DisplayName;
                        }
                    }
                    else
                    {
                        AvailablePrompts.Insert(i, target);
                    }
                }
            }
            else
            {
                AvailablePrompts.Add(target);
            }
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
        draft.OnEdited = OnProfileEdited;
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

    public void MoveProfile(int oldIndex, int newIndex)
    {
        if (!CanEdit || IsSaving)
        {
            return;
        }

        if (oldIndex < 0 || oldIndex >= Profiles.Count || newIndex < 0 || newIndex >= Profiles.Count || oldIndex == newIndex)
        {
            return;
        }

        Profiles.Move(oldIndex, newIndex);
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
            MoveProfile(idx, idx - 1);
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
            MoveProfile(idx, idx + 1);
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
                    LogTestDraftException(_logger, ex.GetType().Name);
                    result = new ProviderTestResult(false, TimeSpan.Zero, null, new ErrorInfo(ErrorCode.Unknown, UiText.SettingsTestDraftFailed, null, null, false, null));
                }

                await _uiDispatcher.InvokeAsync(() =>
                {
                    lock (_admissionLock)
                    {
                        if (_disposed || _isStopped) return Task.CompletedTask;
                    }

                    if (!ReferenceEquals(_sessionOwner, sessionId)) return Task.CompletedTask;
                    if (SelectedProfile != target) return Task.CompletedTask;
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
                        target.TestStatusMessage = UiText.SettingsTestDraftFailed;
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
                    LogListModelsException(_logger, ex.GetType().Name);
                    errorMsg = UiText.SettingsListModelsFailed;
                }
                catch (Exception ex)
                {
                    models = [];
                    LogListModelsException(_logger, ex.GetType().Name);
                    errorMsg = UiText.SettingsListModelsFailed;
                }

                await _uiDispatcher.InvokeAsync(() =>
                {
                    lock (_admissionLock)
                    {
                        if (_disposed || _isStopped) return Task.CompletedTask;
                    }

                    if (!ReferenceEquals(_sessionOwner, sessionId)) return Task.CompletedTask;
                    if (SelectedProfile != target) return Task.CompletedTask;
                    if (target.Revision != rev || reqSeq != _requestSeq) return Task.CompletedTask;

                    if (errorMsg == null)
                    {
                        var currentModel = target.Model;
                        target.AvailableModels.Clear();
                        foreach (var m in models)
                        {
                            target.AvailableModels.Add(m);
                        }
                        if (!string.IsNullOrEmpty(currentModel))
                        {
                            target.Model = currentModel;
                        }
                        target.ListModelsStatusMessage = $"成功获取 {models.Count} 个模型";
                    }
                    else
                    {
                        target.ListModelsStatusMessage = errorMsg;
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
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanAccessHotkeysTab));
        OnPropertyChanged(nameof(CanRetryHotkeysOrCleanup));
        OnPropertyChanged(nameof(HasFailedRecordingCleanup));
        (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RetryAutoStartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryPendingApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryHotkeysCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryMouseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RaiseProfileCommandsCanExecute();
        RaiseOcrCommands();
        RaiseNetworkCommands();
        foreach (var h in Hotkeys)
        {
            h.RaiseCanRecordChanged();
        }
    }

    private void FreezeAllDrafts()
    {
        Selection.IsFrozen = true;
        foreach (var p in Profiles)
        {
            p.IsFrozen = true;
        }
        if (SelectedProfile != null)
        {
            SelectedProfile.IsFrozen = true;
        }
        foreach (var h in Hotkeys)
        {
            h.IsFrozen = true;
        }
    }

    private void UnfreezeAllDraftsIfAlive()
    {
        bool isAlive = !_disposed && !_isStopped;
        Selection.IsFrozen = !isAlive;
        foreach (var p in Profiles)
        {
            p.IsFrozen = !isAlive;
        }
        if (SelectedProfile != null)
        {
            SelectedProfile.IsFrozen = !isAlive;
        }
        foreach (var h in Hotkeys)
        {
            h.IsFrozen = !isAlive;
        }
    }

    private Task CompleteSaveOrRetryOperationAsync()
    {
        bool isAlive;
        lock (_admissionLock)
        {
            IsSaving = false;
            isAlive = !_disposed && !_isStopped;
            UnfreezeAllDraftsIfAlive();
        }

        if (isAlive)
        {
            OnPropertyChanged(nameof(CanEdit));
            RaiseAllCommandsCanExecute();
        }
        return Task.CompletedTask;
    }

    internal void StopAcceptance()
    {
        bool changed;
        lock (_admissionLock)
        {
            changed = !_isStopped;
            _isStopped = true;
            ++_queryGeneration;
            FreezeAllDrafts();
        }
        if (changed)
        {
            InvalidateNetworkTest();
            Interlocked.Increment(ref _ocrRequestSeq);
            _ocrOperationQueue.CancelInFlight();
            if (_ocrTest != null) OcrTestStatus = UiText.OcrEnding;
        }
        if (changed && _uiDispatcher.CheckAccess()) RaiseAllCommandsCanExecute();
        if (IsAnyRecording)
        {
            _ = CancelRecordingAsync();
        }
    }

    internal async Task WaitForActiveWorkAsync()
    {
        StopAcceptance();
        await _operationQueue.DrainAndCancelAllAsync().ConfigureAwait(false);
        bool ocrDrained = await DrainOcrTestsAsync(closing: true).ConfigureAwait(false);
        bool networkDrained = await DrainNetworkTestsAsync(closing: true).ConfigureAwait(false);

        Exception? firstEx = !networkDrained ? new InvalidOperationException(UiText.NetworkCleanupFailed)
            : ocrDrained ? null : new InvalidOperationException(UiText.OcrEndFailed);
        while (true)
        {
            Task[] tasks;
            lock (_admissionLock)
            {
                _activeWork.RemoveWhere(t => t.IsCompleted);
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
            catch (Exception ex)
            {
                firstEx ??= ex;
            }
        }

        if (HasFailedRecordingCleanup)
        {
            firstEx ??= new InvalidOperationException("快捷键录入清理失败，必须保留会话与作用域供重试");
        }

        if (firstEx != null)
        {
            ExceptionDispatchInfo.Capture(firstEx).Throw();
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
        lock (_admissionLock)
        {
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
            {
                return;
            }
        }

        SettingsEditPlan? plan = null;
        bool wasLaunchAtStartupDirty = false;
        bool targetLaunchAtStartup = false;
        TaskCompletionSource? workTcs = null;

        try
        {
            if (IsAnyRecording)
            {
                await CancelRecordingAsync().ConfigureAwait(false);
            }

            await WaitForActiveRecordingAsync().ConfigureAwait(false);

            if (HasFailedRecordingCleanup)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "快捷键录入清理未完成，无法保存";
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            bool shouldReturn = false;
            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_admissionLock)
                {
                    if (_disposed || _isStopped || IsSaving)
                    {
                        shouldReturn = true;
                        return Task.CompletedTask;
                    }
                }

                BeforeSave?.Invoke();
                if (!Network.TryCapture(out var networkPatch))
                { StatusMessage = Network.ErrorMessage; shouldReturn = true; return Task.CompletedTask; }
                if (!History.TryCapture(out var historyPatch))
                {
                    StatusMessage = History.ErrorMessage;
                    shouldReturn = true;
                    return Task.CompletedTask;
                }
                if (!Selection.TryCapture(out var selectionPatch))
                {
                    StatusMessage = Selection.ErrorMessage;
                    shouldReturn = true;
                    return Task.CompletedTask;
                }

                if (!TryCaptureOcr(out var ocrPatch, out var ocrChanges, out var screenshotPatch))
                {
                    shouldReturn = true;
                    return Task.CompletedTask;
                }

                // 仅校验被编辑或新增的服务；未触及服务不阻止合法保存
                foreach (var profile in Profiles)
                {
                    if (profile.IsNew || profile.IsDirty)
                    {
                        var valErr = profile.ValidateForSave();
                        if (valErr != null)
                        {
                            StatusMessage = $"{profile.DisplayName}: {valErr}";
                            shouldReturn = true;
                            return Task.CompletedTask;
                        }
                    }
                }

                // 校验快捷键草稿
                ValidateHotkeyDrafts();
                if (Hotkeys.Any(h => h.HasDraftError))
                {
                    StatusMessage = "请先修正快捷键冲突或错误";
                    shouldReturn = true;
                    return Task.CompletedTask;
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

                // 组装 Hotkey 增量变更
                var hotkeyChanges = new List<HotkeyBindingChange>();
                foreach (var h in Hotkeys)
                {
                    if (h.IsDirty)
                    {
                        var target = h.TargetBinding ?? new HotkeyBinding(HotkeyModifiers.None, string.Empty, false);
                        hotkeyChanges.Add(new HotkeyBindingChange(h.Action, h.BaseBinding, target));
                    }
                }

                bool hasGeneralDirty = _themeDirty || _launchAtStartupDirty || _showStartupNotificationDirty || _logLevelDirty;
                bool hasTranslationDirty = translationChanges.Count > 0 || orderChange != null;
                bool hasHotkeyDirty = hotkeyChanges.Count > 0;

                if (!hasGeneralDirty && !hasTranslationDirty && !hasHotkeyDirty && historyPatch == null && selectionPatch == null && ocrPatch == null && ocrChanges.Count == 0 && screenshotPatch == null && networkPatch == null
                    && !HasNetworkTestOwnership && !HasOcrTestOwnership && !_operationQueue.HasActiveWork)
                {
                    _themeService.ClearPreview(_sessionOwner);
                    StatusMessage = UiText.SettingsStatusSaved;
                    IsConflict = false;
                    OnPropertyChanged(nameof(IsDirty));
                    shouldReturn = true;
                    return Task.CompletedTask;
                }

                wasLaunchAtStartupDirty = _launchAtStartupDirty;
                targetLaunchAtStartup = _targetLaunchAtStartup;
                plan = new SettingsEditPlan(generalPatch, translationChanges, orderChange)
                {
                    HotkeyChanges = hotkeyChanges,
                    Selection = selectionPatch,
                    History = historyPatch,
                    Network = networkPatch,
                    Ocr = ocrPatch, OcrChanges = ocrChanges, Screenshot = screenshotPatch
                };

                lock (_admissionLock)
                {
                    if (_disposed || _isStopped)
                    {
                        shouldReturn = true;
                        return Task.CompletedTask;
                    }
                    ++_queryGeneration;
                    workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _activeWork.Add(workTcs.Task);
                    IsSaving = true;
                    FreezeAllDrafts();
                }

                StatusMessage = UiText.SettingsStatusSaving;
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);

            if (shouldReturn || workTcs == null || plan == null)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            HandleCommandException("Save", ex);
            return;
        }

        try
        {
            // 保存前先取消并观察在途草稿测试/模型列表任务退出
            await _operationQueue.DrainAndCancelInFlightAsync().ConfigureAwait(false);
            bool ocrDrained = await DrainOcrTestsAsync(closing: false).ConfigureAwait(false);
            bool networkDrained = await DrainNetworkTestsAsync(closing: false).ConfigureAwait(false);
            if (!ocrDrained || !networkDrained)
            {
                await _uiDispatcher.InvokeAsync(() => { StatusMessage = !networkDrained ? UiText.NetworkSaveBlocked : UiText.OcrSaveBlocked; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            lock (_admissionLock)
            {
                if (_disposed || _isStopped)
                {
                    return;
                }
            }

            var result = await _saveCoordinator.CommitPlanAsync(plan, wasLaunchAtStartupDirty, targetLaunchAtStartup, CancellationToken.None).ConfigureAwait(false);
            if (result.Status != GeneralSaveStatus.Uncommitted && _mouseController != null)
                await _mouseController.WaitForApplyAsync().ConfigureAwait(false);

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
                        foreach (var p in Profiles)
                        {
                            p.ResetCredentialsToKeepAfterSave();
                        }
                        foreach (var d in _deletedProfiles)
                        {
                            d.ClearReplacementApiKey();
                        }
                        _deletedProfiles.Clear();
                        foreach (var h in Hotkeys)
                        {
                            h.SyncBaseline(h.TargetBinding);
                        }
                        IsConflict = false;
                        if (wasLaunchAtStartupDirty)
                        {
                            CanRetryAutoStart = false;
                            StatusMessage = UiText.SettingsStatusSaved;
                        }
                        else
                        {
                            StatusMessage = CanRetryAutoStart ? UiText.SettingsStatusSavedAutoStartStillPending : UiText.SettingsStatusSaved;
                        }
                        var snapshot = _hotkeyCoordinator?.GetRuntimeSnapshot();
                        if (snapshot != null && snapshot.HasAnyFailure)
                        {
                            CanRetryHotkeys = true;
                            StatusMessage = UiText.SettingsHotkeyStatusSavedPartialFailed;
                        }
                        else if (snapshot != null && snapshot.IsPaused)
                        {
                            StatusMessage = UiText.SettingsHotkeyStatusSavedPending;
                        }
                        _ = QueryAutoStartStatusAsync();
                        OnPropertyChanged(nameof(IsDirty));
                        break;

                    case GeneralSaveStatus.SavedApplicationPending:
                        _themeService.ClearPreview(_sessionOwner);
                        SyncBaselineFromCurrent();
                        foreach (var p in Profiles)
                        {
                            p.ResetCredentialsToKeepAfterSave();
                        }
                        foreach (var d in _deletedProfiles)
                        {
                            d.ClearReplacementApiKey();
                        }
                        _deletedProfiles.Clear();
                        foreach (var h in Hotkeys)
                        {
                            h.SyncBaseline(h.TargetBinding);
                        }
                        StatusMessage = result.FailureReason ?? UiText.SettingsStatusSavedPendingApply;
                        IsConflict = false;
                        if (_receiptManager?.HasPendingReceipts == true)
                        {
                            CanRetryPendingApply = true;
                        }
                        if (wasLaunchAtStartupDirty)
                        {
                            CanRetryAutoStart = false;
                        }
                        var snapshotPending = _hotkeyCoordinator?.GetRuntimeSnapshot();
                        if (snapshotPending != null && snapshotPending.HasAnyFailure)
                        {
                            CanRetryHotkeys = true;
                        }
                        _ = QueryAutoStartStatusAsync();
                        OnPropertyChanged(nameof(IsDirty));
                        break;

                    case GeneralSaveStatus.SavedPendingApplyAndAutoStart:
                        _themeService.ClearPreview(_sessionOwner);
                        SyncBaselineFromCurrent();
                        foreach (var p in Profiles)
                        {
                            p.ResetCredentialsToKeepAfterSave();
                        }
                        foreach (var d in _deletedProfiles)
                        {
                            d.ClearReplacementApiKey();
                        }
                        _deletedProfiles.Clear();
                        foreach (var h in Hotkeys)
                        {
                            h.SyncBaseline(h.TargetBinding);
                        }
                        StatusMessage = UiText.SettingsStatusSavedPendingApplyAndAutoStart;
                        IsConflict = false;
                        CanRetryPendingApply = true;
                        CanRetryAutoStart = true;
                        var snapshotBoth = _hotkeyCoordinator?.GetRuntimeSnapshot();
                        if (snapshotBoth != null && snapshotBoth.HasAnyFailure)
                        {
                            CanRetryHotkeys = true;
                        }
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
                await _uiDispatcher.InvokeAsync(CompleteSaveOrRetryOperationAsync, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                lock (_admissionLock)
                {
                    IsSaving = false;
                    UnfreezeAllDraftsIfAlive();
                }
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
        MergeOcrSettings(reset: true);
        MergeNetworkSettings(reset: true);
        Selection.Merge(_settingsService.Current.Selection, reset: true);
        History.Merge(_settingsService.Current.History, reset: true);
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

        // 重建 Hotkeys 基线
        SyncHotkeysFromCurrent();
    }

    private void SyncHotkeysFromCurrent()
    {
        if (Hotkeys.Count == 0)
        {
            LoadHotkeysFromCurrent();
            return;
        }

        var currentHotkeys = _settingsService.Current.Hotkeys;
        foreach (var item in Hotkeys)
        {
            currentHotkeys.TryGetValue(item.Action, out var binding);
            item.SyncBaseline(binding);
        }

        SyncRuntimeStatesFromCoordinator();
        ValidateHotkeyDrafts();
    }

    public void ExecuteCancel()
    {
        if (IsSaving || _disposed || _isStopped)
        {
            return;
        }

        if (IsAnyRecording)
        {
            _ = CancelRecordingAsync();
        }

        foreach (var p in Profiles)
        {
            p.ClearReplacementApiKey();
        }
        foreach (var p in _deletedProfiles)
        {
            p.ClearReplacementApiKey();
        }
        _deletedProfiles.Clear();

        foreach (var h in Hotkeys)
        {
            h.RevertToBaseline();
        }
        ValidateHotkeyDrafts();

        _themeService.ClearPreview(_sessionOwner);
        RequestClose?.Invoke();
    }

    public void ExecuteReload()
    {
        if (IsReadOnly || IsSaving || _disposed || _isStopped)
        {
            return;
        }

        if (IsAnyRecording)
        {
            _ = CancelRecordingAsync();
        }

        if (HasOcrTestOwnership || HasNetworkTestOwnership || _operationQueue.HasActiveWork) { _ = ReloadAfterDraftCleanupAsync(); return; }

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

    internal void SetWindowHandleProvider(Func<nint> provider)
    {
        _windowHandleProvider = provider;
    }

    private void LoadHotkeysFromCurrent()
    {
        Hotkeys.Clear();

        var currentHotkeys = _settingsService.Current.Hotkeys;

        // 1. InputTranslate
        currentHotkeys.TryGetValue(IrisAction.InputTranslate, out var inputBinding);
        var inputItem = new HotkeyItemDraftViewModel(
            IrisAction.InputTranslate,
            UiText.TrayMenuInputTranslate,
            "打开输入翻译窗口快速翻译文本",
            inputBinding,
            this);
        Hotkeys.Add(inputItem);

        // 2. ClipboardTranslate
        currentHotkeys.TryGetValue(IrisAction.ClipboardTranslate, out var clipBinding);
        var clipItem = new HotkeyItemDraftViewModel(
            IrisAction.ClipboardTranslate,
            UiText.TrayMenuClipboardTranslate,
            "读取剪贴板内容并进行翻译",
            clipBinding,
            this);
        Hotkeys.Add(clipItem);

        // 3. OpenSettings (若无配置则 BaseBinding = null，显示未设置/禁用，不能在打开页面时写入默认值)
        currentHotkeys.TryGetValue(IrisAction.OpenSettings, out var settingsBinding);
        var settingsItem = new HotkeyItemDraftViewModel(
            IrisAction.OpenSettings,
            UiText.SettingsTitle,
            "打开 Iris 设置界面",
            settingsBinding,
            this);
        Hotkeys.Add(settingsItem);

        currentHotkeys.TryGetValue(IrisAction.SelectionTranslate, out var selectionBinding);
        Hotkeys.Add(new HotkeyItemDraftViewModel(IrisAction.SelectionTranslate, UiText.SettingsNavSelection,
            UiText.SelectionHotkeyDescription, selectionBinding, this));
        currentHotkeys.TryGetValue(IrisAction.ToggleMouseSelection, out var toggleBinding);
        Hotkeys.Add(new HotkeyItemDraftViewModel(IrisAction.ToggleMouseSelection, UiText.MouseToggle,
            UiText.MouseToggleDescription, toggleBinding, this));

        SyncRuntimeStatesFromCoordinator();
        ValidateHotkeyDrafts();
    }

    private void ValidateHotkeyDrafts()
    {
        var currentHotkeys = _settingsService.Current.Hotkeys;

        foreach (var item in Hotkeys)
        {
            var target = item.TargetBinding;
            if (target == null || !target.Enabled || string.IsNullOrWhiteSpace(target.Key))
            {
                item.DraftError = null;
                continue;
            }

            // 1. Policy 规范校验
            if (!_hotkeyBindingPolicy.TryNormalize(target, out _))
            {
                item.DraftError = UiText.SettingsHotkeyStatusInvalid;
                continue;
            }

            // 2. 检查与其他草稿项冲突
            bool conflictFound = false;
            foreach (var other in Hotkeys)
            {
                if (ReferenceEquals(item, other)) continue;
                var otherTarget = other.TargetBinding;
                if (otherTarget != null && otherTarget.Enabled && !string.IsNullOrWhiteSpace(otherTarget.Key))
                {
                    if (otherTarget.Modifiers == target.Modifiers &&
                        string.Equals(otherTarget.Key?.Trim(), target.Key?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        item.DraftError = string.Format(CultureInfo.CurrentCulture, s_hotkeyDuplicateConflictFormat, other.DisplayName);
                        conflictFound = true;
                        break;
                    }
                }
            }

            if (conflictFound) continue;

            // 3. 检查与尚未开放动作的预留配置冲突
            foreach (var (reservedAction, reservedBinding) in currentHotkeys)
            {
                if (Hotkeys.Any(item => item.Action == reservedAction))
                {
                    continue;
                }

                if (reservedBinding.Enabled && !string.IsNullOrWhiteSpace(reservedBinding.Key))
                {
                    if (reservedBinding.Modifiers == target.Modifiers &&
                        string.Equals(reservedBinding.Key?.Trim(), target.Key?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        string reservedName = GetActionDisplayName(reservedAction);
                        item.DraftError = string.Format(CultureInfo.CurrentCulture, s_hotkeyReservedConflictFormat, reservedName);
                        conflictFound = true;
                        break;
                    }
                }
            }

            if (!conflictFound)
            {
                item.DraftError = null;
            }
        }
    }

    internal void OnHotkeyDraftChanged(HotkeyItemDraftViewModel item)
    {
        ValidateHotkeyDrafts();
        OnPropertyChanged(nameof(IsDirty));
        RaiseAllCommandsCanExecute();
    }

    public async Task StartRecordingAsync(HotkeyItemDraftViewModel item)
    {
        if (!CanEdit || !CanStartRecording || IsSaving || _activeRecordingItem != null || item.IsFrozen)
        {
            return;
        }

        nint hwnd = _windowHandleProvider?.Invoke() ?? 0;
        if (hwnd == 0)
        {
            return;
        }

        long preparationEpoch = _recordingCoordinator?.PreparationEpoch ?? 0;
        long currentSeq = Interlocked.Increment(ref _recordingSessionSeq);
        _activeRecordingItem = item;
        item.SetRecordingState(true, true, UiText.SettingsHotkeyPreparing, null);
        RaiseAllCommandsCanExecute();

        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (_disposed || _isStopped) return;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRecordingTask = workTcs.Task;
            _activeWork.Add(workTcs.Task);
        }

        Exception? caughtEx = null;
        try
        {
            if (_recordingCoordinator != null)
            {
                var result = await _recordingCoordinator.RecordAsync(
                    _sessionOwner.ToString()!,
                    hwnd,
                    progress =>
                    {
                        _ = _uiDispatcher.InvokeAsync(() =>
                        {
                            if (_recordingSessionSeq != currentSeq || _disposed || _isStopped || preparationEpoch != (_recordingCoordinator?.PreparationEpoch ?? 0))
                            {
                                return Task.CompletedTask;
                            }

                            if (ReferenceEquals(_activeRecordingItem, item))
                            {
                                if (progress.Error != null)
                                {
                                    item.DraftError = progress.Error.Message;
                                }

                                if (!progress.IsReady)
                                {
                                    item.SetRecordingState(true, true, UiText.SettingsHotkeyReleasePrompt, null);
                                }
                                else
                                {
                                    string preview = HotkeyCoordinator.FormatKeyCombo(progress.Modifiers, progress.Key);
                                    item.SetRecordingState(true, false, UiText.SettingsHotkeyRecordingPrompt, preview);
                                }
                            }
                            return Task.CompletedTask;
                        }, CancellationToken.None);
                    },
                    CancellationToken.None);

                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_recordingSessionSeq != currentSeq || _disposed || _isStopped || preparationEpoch != (_recordingCoordinator?.PreparationEpoch ?? 0))
                    {
                        return Task.CompletedTask;
                    }

                    if (_activeRecordingItem == null || ReferenceEquals(_activeRecordingItem, item))
                    {
                        switch (result.Outcome)
                        {
                            case HotkeyCaptureOutcome.Captured:
                                if (result.Binding != null)
                                {
                                    item.ApplyCapturedBinding(result.Binding);
                                }
                                break;
                            case HotkeyCaptureOutcome.Cleared:
                                item.ApplyClearedBinding();
                                break;
                            case HotkeyCaptureOutcome.Failed:
                                item.DraftError = result.Error?.Message ?? UiText.SettingsHotkeyStatusFailed;
                                break;
                            case HotkeyCaptureOutcome.Canceled:
                            default:
                                break;
                        }
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            caughtEx = ex;
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_recordingSessionSeq == currentSeq && ReferenceEquals(_activeRecordingItem, item))
                {
                    if (HasFailedRecordingCleanup)
                    {
                        item.DraftError = UiText.SettingsHotkeyCleanupFailed;
                    }
                    else
                    {
                        item.DraftError = (ex as IrisException)?.Message ?? UiText.SettingsHotkeyStatusFailed;
                    }
                }
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        finally
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_recordingSessionSeq == currentSeq)
                    {
                        item.SetRecordingState(false, false, null, null);
                        if (!HasFailedRecordingCleanup)
                        {
                            _activeRecordingItem = null;
                            if (item.DraftError == null)
                            {
                                ValidateHotkeyDrafts();
                            }
                        }
                        RaiseAllCommandsCanExecute();
                        OnPropertyChanged(nameof(CanEdit));
                        OnPropertyChanged(nameof(CanSave));
                        OnPropertyChanged(nameof(IsAnyRecording));
                        OnPropertyChanged(nameof(CanAccessHotkeysTab));
                        OnPropertyChanged(nameof(CanRetryHotkeysOrCleanup));
                        OnPropertyChanged(nameof(HasFailedRecordingCleanup));
                        OnPropertyChanged(nameof(IsDirty));
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
            finally
            {
                lock (_admissionLock)
                {
                    _activeWork.Remove(workTcs.Task);
                }

                if (!HasFailedRecordingCleanup)
                {
                    _activeRecordingTask = null;
                    workTcs.TrySetResult();
                }
                else
                {
                    workTcs.TrySetException(caughtEx ?? new InvalidOperationException("结束录入清理失败"));
                }
            }
        }
    }

    internal async Task CancelRecordingForPreparationAsync()
    {
        var item = _activeRecordingItem;
        var task = _activeRecordingTask;
        Interlocked.Increment(ref _recordingSessionSeq);
        Exception? failure = null;
        try
        {
            if (_recordingCoordinator != null) await _recordingCoordinator.CancelForPreparationAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        if (task != null)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { if (HasFailedRecordingCleanup) failure ??= ex; }
        }
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_activeRecordingItem, item))
            {
                item?.SetRecordingState(false, false, null, null);
                if (HasFailedRecordingCleanup)
                {
                    if (item != null) item.DraftError = UiText.SettingsHotkeyCleanupFailed;
                }
                else { _activeRecordingItem = null; _activeRecordingTask = null; }
                RaiseAllCommandsCanExecute();
                OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(IsAnyRecording)); OnPropertyChanged(nameof(HasFailedRecordingCleanup));
                OnPropertyChanged(nameof(CanRetryHotkeysOrCleanup));
            }
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
        if (failure != null) throw new IrisException(ErrorCode.CaptureFailed, ErrorMessages.GetDefault(ErrorCode.CaptureFailed),
            detail: $"stage=RECORDING_CLEANUP;hr=0x{failure.HResult:X8}");
    }

    public async Task CancelRecordingAsync()
    {
        if (_recordingCoordinator != null)
        {
            await _recordingCoordinator.CancelRecordingAsync().ConfigureAwait(false);
        }
    }

    internal async Task WaitForActiveRecordingAsync()
    {
        var task = _activeRecordingTask;
        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public async Task ExecuteRetryHotkeysAsync()
    {
        if ((!CanEdit && !HasFailedRecordingCleanup) || IsSaving || (_hotkeyCoordinator == null && _recordingCoordinator == null))
        {
            return;
        }

        TaskCompletionSource workTcs;
        bool wasStoppedInitially;
        lock (_admissionLock)
        {
            if (_disposed || (_isStopped && !HasFailedRecordingCleanup)) return;
            wasStoppedInitially = _isStopped;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
            IsSaving = true;
            FreezeAllDrafts();
        }

        StatusMessage = UiText.SettingsStatusRetryingHotkeys;

        try
        {
            if (_recordingCoordinator != null && _recordingCoordinator.HasFailedCleanup)
            {
                var targetItem = _activeRecordingItem;
                long targetSeq = _recordingSessionSeq;
                var retryResult = await _recordingCoordinator.RetryCleanupAsync().ConfigureAwait(false);
                await _uiDispatcher.InvokeAsync(() =>
                {
                    lock (_admissionLock)
                    {
                        if (_disposed) return Task.CompletedTask;
                    }

                    if (_recordingSessionSeq == targetSeq && ReferenceEquals(_activeRecordingItem, targetItem) && targetItem != null)
                    {
                        if (!_isStopped)
                        {
                            if (retryResult?.Outcome == HotkeyCaptureOutcome.Captured && retryResult.Binding != null)
                            {
                                targetItem.ApplyCapturedBinding(retryResult.Binding);
                            }
                            else if (retryResult?.Outcome == HotkeyCaptureOutcome.Cleared)
                            {
                                targetItem.ApplyClearedBinding();
                            }
                        }
                        targetItem.DraftError = null;
                        targetItem.SetRecordingState(false, false, null, null);
                        _activeRecordingItem = null;
                    }
                    else if (targetItem != null)
                    {
                        targetItem.SetRecordingState(false, false, null, null);
                    }

                    if (_activeRecordingItem != null)
                    {
                        _activeRecordingItem.SetRecordingState(false, false, null, null);
                        _activeRecordingItem = null;
                    }

                    _activeRecordingTask = null;
                    ValidateHotkeyDrafts();
                    RaiseAllCommandsCanExecute();
                    OnPropertyChanged(nameof(CanEdit));
                    OnPropertyChanged(nameof(CanSave));
                    OnPropertyChanged(nameof(IsAnyRecording));
                    OnPropertyChanged(nameof(CanAccessHotkeysTab));
                    OnPropertyChanged(nameof(CanRetryHotkeysOrCleanup));
                    OnPropertyChanged(nameof(HasFailedRecordingCleanup));
                    OnPropertyChanged(nameof(IsDirty));
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }

            if (_hotkeyCoordinator != null)
            {
                var snapshot = await _hotkeyCoordinator.RetryApplyAsync().ConfigureAwait(false);

                await _uiDispatcher.InvokeAsync(() =>
                {
                    lock (_admissionLock)
                    {
                        if (_disposed || _isStopped) return Task.CompletedTask;
                    }

                    if (snapshot.SnapshotId >= _lastAppliedHotkeySnapshotId)
                    {
                        _lastAppliedHotkeySnapshotId = snapshot.SnapshotId;
                        foreach (var item in Hotkeys)
                        {
                            if (snapshot.Items.TryGetValue(item.Action, out var state))
                            {
                                item.RuntimeState = state;
                            }
                        }

                        if (snapshot.HasAnyFailure)
                        {
                            StatusMessage = UiText.SettingsStatusHotkeysRetryFailed;
                            CanRetryHotkeys = true;
                        }
                        else
                        {
                            StatusMessage = UiText.SettingsStatusHotkeysApplied;
                            CanRetryHotkeys = false;
                        }
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleCommandException("RetryHotkeys", ex);
        }
        finally
        {
            try
            {
                await _uiDispatcher.InvokeAsync(CompleteSaveOrRetryOperationAsync, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                lock (_admissionLock)
                {
                    IsSaving = false;
                    UnfreezeAllDraftsIfAlive();
                }
            }
            finally
            {
                bool shouldResumeClose;
                lock (_admissionLock)
                {
                    _activeWork.Remove(workTcs.Task);
                    shouldResumeClose = wasStoppedInitially && !HasFailedRecordingCleanup && !_disposed;
                }
                workTcs.TrySetResult();

                if (shouldResumeClose)
                {
                    _ = _uiDispatcher.InvokeAsync(() =>
                    {
                        RequestClose?.Invoke();
                        return Task.CompletedTask;
                    }, CancellationToken.None);
                }
            }
        }
    }

    private long _lastAppliedHotkeySnapshotId = -1;

    private void OnHotkeyRuntimeSnapshotChanged(object? sender, HotkeyRuntimeSnapshot snapshot)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            lock (_admissionLock)
            {
                if (_disposed || _isStopped) return Task.CompletedTask;
            }

            if (snapshot.SnapshotId < _lastAppliedHotkeySnapshotId)
            {
                return Task.CompletedTask;
            }
            _lastAppliedHotkeySnapshotId = snapshot.SnapshotId;

            foreach (var item in Hotkeys)
            {
                if (snapshot.Items.TryGetValue(item.Action, out var state))
                {
                    item.RuntimeState = state;
                }
            }

            CanRetryHotkeys = snapshot.HasAnyFailure;
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private void SyncRuntimeStatesFromCoordinator()
    {
        if (_hotkeyCoordinator == null) return;
        var snapshot = _hotkeyCoordinator.GetRuntimeSnapshot();
        if (snapshot.SnapshotId < _lastAppliedHotkeySnapshotId)
        {
            return;
        }
        _lastAppliedHotkeySnapshotId = snapshot.SnapshotId;

        foreach (var item in Hotkeys)
        {
            if (snapshot.Items.TryGetValue(item.Action, out var state))
            {
                item.RuntimeState = state;
            }
        }
        CanRetryHotkeys = snapshot.HasAnyFailure;
    }

    private void OnExternalHotkeysChanged(IReadOnlyDictionary<IrisAction, HotkeyBinding> newHotkeys)
    {
        foreach (var item in Hotkeys)
        {
            newHotkeys.TryGetValue(item.Action, out var newBase);
            item.UpdateBaselineFromExternal(newBase);
        }
        ValidateHotkeyDrafts();
        OnPropertyChanged(nameof(IsDirty));
    }

    private static string GetActionDisplayName(IrisAction action) => action switch
    {
        IrisAction.InputTranslate => UiText.TrayMenuInputTranslate,
        IrisAction.ClipboardTranslate => UiText.TrayMenuClipboardTranslate,
        IrisAction.TranslateText => "翻译文本",
        IrisAction.OpenSettings => UiText.SettingsTitle,
        IrisAction.SelectionTranslate => "划词翻译",
        IrisAction.ScreenshotTranslate => "截图翻译",
        IrisAction.ScreenshotOcr => "截图 OCR",
        IrisAction.SilentScreenshotOcr => "静默截图 OCR",
        IrisAction.ToggleFloatingBall => "悬浮球",
        _ => action.ToString(),
    };

    private sealed class DefaultHotkeyBindingPolicy : IHotkeyBindingPolicy
    {
        public bool TryNormalize(HotkeyBinding? binding, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HotkeyBinding? normalized)
        {
            normalized = null;
            if (binding == null) return false;
            if (!binding.Enabled && string.IsNullOrWhiteSpace(binding.Key) && binding.Modifiers == HotkeyModifiers.None)
            {
                normalized = new HotkeyBinding(HotkeyModifiers.None, string.Empty, false);
                return true;
            }
            if (KeyNameMapper.ValidateBinding(binding, out _) &&
                KeyNameMapper.TryGetCanonicalKeyName(binding.Key, out var canonicalKey))
            {
                normalized = new HotkeyBinding(binding.Modifiers, canonicalKey, binding.Enabled);
                return true;
            }
            return false;
        }
    }

    public async Task ExecuteRetryAutoStartAsync()
    {
        TaskCompletionSource workTcs;
        lock (_admissionLock)
        {
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
            {
                return;
            }
            ++_queryGeneration;
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
            IsSaving = true;
            FreezeAllDrafts();
        }

        StatusMessage = UiText.SettingsStatusRetryingAutoStart;

        var targetStartup = _settingsService.Current.General.LaunchAtStartup;

        try
        {
            var result = await _saveCoordinator.RetryAutoStartAsync().ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {
                lock (_admissionLock)
                {
                    if (_disposed || _isStopped)
                    {
                        return Task.CompletedTask;
                    }
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
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    HandleCommandException("RetryAutoStart", ex);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                HandleCommandException("RetryAutoStart", ex);
            }
        }
        finally
        {
            try
            {
                await _uiDispatcher.InvokeAsync(CompleteSaveOrRetryOperationAsync, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                lock (_admissionLock)
                {
                    IsSaving = false;
                    UnfreezeAllDraftsIfAlive();
                }
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
            if (IsReadOnly || IsSaving || _disposed || _isStopped)
            {
                return;
            }
            workTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWork.Add(workTcs.Task);
            IsSaving = true;
            FreezeAllDrafts();
        }

        StatusMessage = UiText.SettingsStatusRetryingPendingApply;

        try
        {
            var pendingSnapshot = _receiptManager.GetPendingReceipts();
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
                    lastError = applyResult.Error?.Message;
                    _receiptManager.UpdateReceiptError(entry.Receipt, lastError);
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

                if (_receiptManager != null && _receiptManager.HasPendingReceipts)
                {
                    StatusMessage = lastError ?? UiText.SettingsStatusPendingApplyFailed;
                    CanRetryPendingApply = true;
                }
                else
                {
                    StatusMessage = UiText.SettingsStatusPendingApplyCompleted;
                    CanRetryPendingApply = false;
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    HandleCommandException("RetryPendingApply", ex);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                HandleCommandException("RetryPendingApply", ex);
            }
        }
        finally
        {
            try
            {
                await _uiDispatcher.InvokeAsync(CompleteSaveOrRetryOperationAsync, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                lock (_admissionLock)
                {
                    IsSaving = false;
                    UnfreezeAllDraftsIfAlive();
                }
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
            var externalProviders = _settingsService.Current.Translation.Providers
                .OrderBy(p => p.Order)
                .ToList();
            var externalMap = externalProviders.ToDictionary(p => p.Id, StringComparer.Ordinal);

            if (!IsOrderDirty)
            {
                using (Profiles.AllowInternalMutation())
                {
                    // 1. 本地未修改排序：列表与基线完整同步外部成员
                    _baselineProfileIds = externalProviders.Select(p => p.Id).ToArray();

                    // 移除已不存在且未编辑的服务
                    for (int i = Profiles.Count - 1; i >= 0; i--)
                    {
                        var local = Profiles[i];
                        if (!externalMap.ContainsKey(local.Id))
                        {
                            if (!local.IsDirty && !local.IsNew)
                            {
                                local.ClearReplacementApiKey();
                                local.PropertyChanged -= OnProfileDraftPropertyChanged;
                                Profiles.RemoveAt(i);
                            }
                        }
                    }

                    // 添加外部新增的服务（且未被本地删除标记）
                    foreach (var ext in externalProviders)
                    {
                        if (!Profiles.Any(p => p.Id == ext.Id) && !_deletedProfiles.Any(d => d.Id == ext.Id))
                        {
                            var newDraft = TranslationProfileDraftViewModel.FromExisting(ext);
                            if (IsReadOnly || IsSaving || _disposed || _isStopped)
                            {
                                newDraft.IsFrozen = true;
                            }
                            newDraft.OnEdited = OnProfileEdited;
                            newDraft.PropertyChanged += OnProfileDraftPropertyChanged;
                            Profiles.Add(newDraft);
                        }
                    }

                    // 更新未编辑服务的基线
                    foreach (var p in Profiles)
                    {
                        if (externalMap.TryGetValue(p.Id, out var updated))
                        {
                            if (!p.IsDirty)
                            {
                                p.SyncBaseline(updated);
                            }
                        }
                    }

                    // 按照 externalProviders 顺序重排 Profiles
                    var orderedProfiles = Profiles
                        .OrderBy(p =>
                        {
                            var idx = externalProviders.FindIndex(ext => ext.Id == p.Id);
                            return idx >= 0 ? idx : int.MaxValue;
                        })
                        .ToList();

                    for (int targetIdx = 0; targetIdx < orderedProfiles.Count; targetIdx++)
                    {
                        var item = orderedProfiles[targetIdx];
                        var currentIdx = Profiles.IndexOf(item);
                        if (currentIdx != targetIdx)
                        {
                            Profiles.Move(currentIdx, targetIdx);
                        }
                    }

                    // 确保 SelectedProfile 有效
                    if (SelectedProfile == null || !Profiles.Contains(SelectedProfile))
                    {
                        SetSelectedProfileInternal(Profiles.FirstOrDefault());
                    }
                }
            }
            else
            {
                // 本地已修改排序：保留本地顺序，只同步未编辑服务的基线
                foreach (var p in Profiles)
                {
                    if (externalMap.TryGetValue(p.Id, out var updated))
                    {
                        if (!p.IsDirty)
                        {
                            p.SyncBaseline(updated);
                        }
                    }
                }
            }

            RefreshAvailablePrompts();
            OnExternalHotkeysChanged(_settingsService.Current.Hotkeys);
            Selection.Merge(_settingsService.Current.Selection);
            History.Merge(_settingsService.Current.History);
            MergeOcrSettings(reset: false);
            MergeNetworkSettings(reset: false);
            OnPropertyChanged(nameof(IsDirty));
            RaiseAllCommandsCanExecute();
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
        DisposeOcrDrafts();
        DisposeNetworkDraft();
        if (_recordingCoordinator != null) _recordingCoordinator.AdmissionChanged -= OnRecordingAdmissionChanged;
        lock (_admissionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isStopped = true;
            ++_queryGeneration;
            FreezeAllDrafts();
        }

        _settingsService.Changed -= OnExternalSettingsChanged;
        if (_mouseController != null) _mouseController.RuntimeChanged -= OnMouseRuntimeChanged;
        if (_receiptManager != null)
        {
            _receiptManager.Changed -= OnReceiptManagerChanged;
        }
        if (_hotkeyCoordinator != null)
        {
            _hotkeyCoordinator.RuntimeSnapshotChanged -= OnHotkeyRuntimeSnapshotChanged;
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

    [LoggerMessage(EventId = 2, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "草稿测试执行异常，异常类型：{ExceptionType}")]
    private static partial void LogTestDraftException(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "获取模型列表异常，异常类型：{ExceptionType}")]
    private static partial void LogListModelsException(ILogger logger, string exceptionType);

    internal sealed class ControlledProfileCollection : ObservableCollection<TranslationProfileDraftViewModel>
    {
        private readonly SettingsViewModel _owner;
        private int _internalMutationScope;

        public ControlledProfileCollection(SettingsViewModel owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public IDisposable AllowInternalMutation()
        {
            Interlocked.Increment(ref _internalMutationScope);
            return new MutationScope(this);
        }

        private bool CanMutate
        {
            get
            {
                if (_internalMutationScope > 0)
                {
                    return true;
                }
                return !_owner.IsSaving && !_owner._isStopped && !_owner._disposed && _owner.CanEdit;
            }
        }

        protected override void ClearItems()
        {
            if (!CanMutate) return;
            base.ClearItems();
        }

        protected override void InsertItem(int index, TranslationProfileDraftViewModel item)
        {
            if (!CanMutate) return;
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            if (!CanMutate) return;
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, TranslationProfileDraftViewModel item)
        {
            if (!CanMutate) return;
            base.SetItem(index, item);
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            if (!CanMutate) return;
            base.MoveItem(oldIndex, newIndex);
        }

        private sealed class MutationScope(ControlledProfileCollection collection) : IDisposable
        {
            private ControlledProfileCollection? _collection = collection;

            public void Dispose()
            {
                var col = Interlocked.Exchange(ref _collection, null);
                if (col != null)
                {
                    Interlocked.Decrement(ref col._internalMutationScope);
                }
            }
        }
    }
}
