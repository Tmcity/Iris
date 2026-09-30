using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyList<AppTheme> _availableThemes = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    private static readonly IReadOnlyList<string> _availableLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];

    private readonly ISettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly GeneralSettingsSaveCoordinator _saveCoordinator;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly object _sessionOwner = new();

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

    private string _currentPageKey = "general";
    private bool _isSaving;
    private string? _statusMessage;
    private bool _isConflict;
    private bool _canRetryAutoStart;
    private bool _disposed;
    private Task? _inFlightTask;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RequestClose;

    public SettingsViewModel(
        ISettingsService settingsService,
        ThemeService themeService,
        GeneralSettingsSaveCoordinator saveCoordinator,
        IUiDispatcher uiDispatcher)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _saveCoordinator = saveCoordinator ?? throw new ArgumentNullException(nameof(saveCoordinator));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

        // 从当前设置初始化基线与目标
        var general = _settingsService.Current.General;
        _baseTheme = general.Theme;
        _targetTheme = general.Theme;

        _baseLaunchAtStartup = general.LaunchAtStartup;
        _targetLaunchAtStartup = general.LaunchAtStartup;

        _baseShowStartupNotification = general.ShowStartupNotification;
        _targetShowStartupNotification = general.ShowStartupNotification;

        _baseLogLevel = general.LogLevel;
        _targetLogLevel = general.LogLevel;

        _canRetryAutoStart = _saveCoordinator.IsAutoStartPending();

        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync, () => CanEdit);
        CancelCommand = new RelayCommand(ExecuteCancel, () => CanEdit);
        ReloadCommand = new RelayCommand(ExecuteReload, () => CanEdit);
        RetryAutoStartCommand = new AsyncRelayCommand(ExecuteRetryAutoStartAsync, () => !IsSaving && CanRetryAutoStart && !_disposed);

        _settingsService.Changed += OnExternalSettingsChanged;
    }

    public static IReadOnlyList<AppTheme> AvailableThemes => _availableThemes;
    public static IReadOnlyList<string> AvailableLogLevels => _availableLogLevels;

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RetryAutoStartCommand { get; }

    public string CurrentPageKey
    {
        get => _currentPageKey;
        set
        {
            var normalized = string.Equals(value, "general", StringComparison.OrdinalIgnoreCase) ? "general" : "general";
            if (_currentPageKey != normalized)
            {
                _currentPageKey = normalized;
                OnPropertyChanged();
            }
        }
    }

    public bool CanEdit => !IsSaving && !_disposed;

    public AppTheme Theme
    {
        get => _targetTheme;
        set
        {
            if (IsSaving || _disposed)
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
            if (IsSaving || _disposed)
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
            if (IsSaving || _disposed)
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
            if (IsSaving || _disposed)
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

    public bool IsDirty => _themeDirty || _launchAtStartupDirty || _showStartupNotificationDirty || _logLevelDirty;

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
                (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ReloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RetryAutoStartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

    internal object SessionOwner => _sessionOwner;

    internal async Task WaitForActiveWorkAsync()
    {
        while (true)
        {
            var task = _inFlightTask;
            if (task == null)
            {
                break;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public async Task ExecuteSaveAsync()
    {
        if (IsSaving || _disposed)
        {
            return;
        }

        IsSaving = true;
        StatusMessage = UiText.SettingsStatusSaving;

        var inFlightTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightTask = inFlightTcs.Task;

        var snapshot = new GeneralDraftSnapshot(
            BaseTheme: _baseTheme,
            TargetTheme: _targetTheme,
            ThemeDirty: _themeDirty,
            BaseLaunchAtStartup: _baseLaunchAtStartup,
            TargetLaunchAtStartup: _targetLaunchAtStartup,
            LaunchAtStartupDirty: _launchAtStartupDirty,
            BaseShowStartupNotification: _baseShowStartupNotification,
            TargetShowStartupNotification: _targetShowStartupNotification,
            ShowStartupNotificationDirty: _showStartupNotificationDirty,
            BaseLogLevel: _baseLogLevel,
            TargetLogLevel: _targetLogLevel,
            LogLevelDirty: _logLevelDirty);

        try
        {
            var result = await _saveCoordinator.SaveAsync(snapshot, CancellationToken.None);

            if (_disposed)
            {
                return;
            }

            switch (result.Status)
            {
                case GeneralSaveStatus.SavedAndApplied:
                    // 保存成功后释放预览（持久主题已生效）
                    _themeService.ClearPreview(_sessionOwner);
                    SyncBaselineFromCurrent();
                    StatusMessage = UiText.SettingsStatusSaved;
                    IsConflict = false;
                    CanRetryAutoStart = _saveCoordinator.IsAutoStartPending();
                    if (CanRetryAutoStart)
                    {
                        StatusMessage = UiText.SettingsStatusSavedAutoStartStillPending;
                    }
                    OnPropertyChanged(nameof(IsDirty));
                    break;

                case GeneralSaveStatus.SavedApplicationPending:
                    // JSON 已提交，开机自启应用未完成
                    _themeService.ClearPreview(_sessionOwner);
                    SyncBaselineFromCurrent();
                    StatusMessage = result.FailureReason ?? UiText.SettingsStatusSavedAutoStartPending;
                    IsConflict = false;
                    CanRetryAutoStart = true;
                    OnPropertyChanged(nameof(IsDirty));
                    break;

                case GeneralSaveStatus.Uncommitted:
                default:
                    // 保存失败或冲突：继续保留预览与草稿
                    StatusMessage = result.FailureReason ?? UiText.SettingsStatusSaveFailed;
                    IsConflict = result.IsConflict;
                    break;
            }
        }
        finally
        {
            _inFlightTask = null;
            inFlightTcs.TrySetResult();
            IsSaving = false;
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
    }

    public void ExecuteCancel()
    {
        if (IsSaving || _disposed)
        {
            return;
        }

        // 取消时结束预览，恢复持久主题
        _themeService.ClearPreview(_sessionOwner);
        RequestClose?.Invoke();
    }

    public void ExecuteReload()
    {
        if (IsSaving || _disposed)
        {
            return;
        }

        SyncBaselineFromCurrent();
        _themeService.ClearPreview(_sessionOwner);

        StatusMessage = null;
        IsConflict = false;
        CanRetryAutoStart = _saveCoordinator.IsAutoStartPending();

        OnPropertyChanged(nameof(IsDirty));
    }

    public async Task ExecuteRetryAutoStartAsync()
    {
        if (IsSaving || !CanRetryAutoStart || _disposed)
        {
            return;
        }

        IsSaving = true;
        StatusMessage = UiText.SettingsStatusRetryingAutoStart;

        try
        {
            var result = await _saveCoordinator.RetryAutoStartAsync();
            if (_disposed)
            {
                return;
            }

            if (result.Status == GeneralSaveStatus.SavedAndApplied)
            {
                StatusMessage = UiText.SettingsStatusAutoStartApplied;
                CanRetryAutoStart = _saveCoordinator.IsAutoStartPending();
            }
            else
            {
                StatusMessage = result.FailureReason ?? UiText.SettingsStatusAutoStartRetryFailed;
                CanRetryAutoStart = true;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void OnExternalSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            var current = _settingsService.Current.General;

            // 未编辑字段跟随最新当前值；已编辑字段保留草稿与原基线以支持冲突检测
            if (!_themeDirty)
            {
                _baseTheme = current.Theme;
                _targetTheme = current.Theme;
                OnPropertyChanged(nameof(Theme));
                if (_themeService.PreviewOwner == _sessionOwner)
                {
                    _themeService.ClearPreview(_sessionOwner);
                }
            }

            if (!_launchAtStartupDirty)
            {
                _baseLaunchAtStartup = current.LaunchAtStartup;
                _targetLaunchAtStartup = current.LaunchAtStartup;
                OnPropertyChanged(nameof(LaunchAtStartup));
            }

            if (!_showStartupNotificationDirty)
            {
                _baseShowStartupNotification = current.ShowStartupNotification;
                _targetShowStartupNotification = current.ShowStartupNotification;
                OnPropertyChanged(nameof(ShowStartupNotification));
            }

            if (!_logLevelDirty)
            {
                _baseLogLevel = current.LogLevel;
                _targetLogLevel = current.LogLevel;
                OnPropertyChanged(nameof(LogLevel));
            }

            OnPropertyChanged(nameof(IsDirty));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.Changed -= OnExternalSettingsChanged;
        _themeService.ClearPreview(_sessionOwner);
        OnPropertyChanged(nameof(CanEdit));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
