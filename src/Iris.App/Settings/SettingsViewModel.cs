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

        SaveCommand = new AsyncRelayCommand(ExecuteSaveAsync, () => !IsSaving);
        CancelCommand = new RelayCommand(ExecuteCancel, () => !IsSaving);
        ReloadCommand = new RelayCommand(ExecuteReload, () => !IsSaving);
        RetryAutoStartCommand = new AsyncRelayCommand(ExecuteRetryAutoStartAsync, () => !IsSaving && CanRetryAutoStart);

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

    public AppTheme Theme
    {
        get => _targetTheme;
        set
        {
            if (_targetTheme != value)
            {
                _targetTheme = value;
                _themeDirty = _targetTheme != _baseTheme;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDirty));
                _themeService.SetPreview(_sessionOwner, _targetTheme);
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _targetLaunchAtStartup;
        set
        {
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

    public async Task ExecuteSaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        IsSaving = true;
        StatusMessage = "正在保存…";

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

            switch (result.Status)
            {
                case GeneralSaveStatus.SavedAndApplied:
                    // 保存成功后释放预览（持久主题已生效）
                    _themeService.ClearPreview(_sessionOwner);
                    _baseTheme = _targetTheme;
                    _themeDirty = false;
                    _baseLaunchAtStartup = _targetLaunchAtStartup;
                    _launchAtStartupDirty = false;
                    _baseShowStartupNotification = _targetShowStartupNotification;
                    _showStartupNotificationDirty = false;
                    _baseLogLevel = _targetLogLevel;
                    _logLevelDirty = false;
                    StatusMessage = "设置已保存";
                    IsConflict = false;
                    CanRetryAutoStart = false;
                    OnPropertyChanged(nameof(IsDirty));
                    break;

                case GeneralSaveStatus.SavedApplicationPending:
                    // JSON 已提交，开机自启应用未完成
                    _themeService.ClearPreview(_sessionOwner);
                    _baseTheme = _targetTheme;
                    _themeDirty = false;
                    _baseLaunchAtStartup = _targetLaunchAtStartup;
                    _launchAtStartupDirty = false;
                    _baseShowStartupNotification = _targetShowStartupNotification;
                    _showStartupNotificationDirty = false;
                    _baseLogLevel = _targetLogLevel;
                    _logLevelDirty = false;
                    StatusMessage = result.FailureReason ?? "已保存，但开机启动设置未生效（可重试）";
                    IsConflict = false;
                    CanRetryAutoStart = true;
                    OnPropertyChanged(nameof(IsDirty));
                    break;

                case GeneralSaveStatus.Uncommitted:
                default:
                    // 保存失败或冲突：继续保留预览与草稿
                    StatusMessage = result.FailureReason ?? "保存失败";
                    IsConflict = result.IsConflict;
                    break;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    public void ExecuteCancel()
    {
        if (IsSaving)
        {
            return;
        }

        // 取消时结束预览，恢复持久主题
        _themeService.ClearPreview(_sessionOwner);
        RequestClose?.Invoke();
    }

    public void ExecuteReload()
    {
        if (IsSaving)
        {
            return;
        }

        var general = _settingsService.Current.General;
        _baseTheme = general.Theme;
        _targetTheme = general.Theme;
        _themeDirty = false;

        _baseLaunchAtStartup = general.LaunchAtStartup;
        _targetLaunchAtStartup = general.LaunchAtStartup;
        _launchAtStartupDirty = false;

        _baseShowStartupNotification = general.ShowStartupNotification;
        _targetShowStartupNotification = general.ShowStartupNotification;
        _showStartupNotificationDirty = false;

        _baseLogLevel = general.LogLevel;
        _targetLogLevel = general.LogLevel;
        _logLevelDirty = false;

        _themeService.ClearPreview(_sessionOwner);

        StatusMessage = null;
        IsConflict = false;
        CanRetryAutoStart = false;

        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(LaunchAtStartup));
        OnPropertyChanged(nameof(ShowStartupNotification));
        OnPropertyChanged(nameof(LogLevel));
        OnPropertyChanged(nameof(IsDirty));
    }

    public async Task ExecuteRetryAutoStartAsync()
    {
        if (IsSaving || !CanRetryAutoStart)
        {
            return;
        }

        IsSaving = true;
        StatusMessage = "正在重试开机启动设置…";

        try
        {
            var result = await _saveCoordinator.RetryAutoStartAsync();
            if (result.Status == GeneralSaveStatus.SavedAndApplied)
            {
                StatusMessage = "开机启动设置已生效";
                CanRetryAutoStart = false;
            }
            else
            {
                StatusMessage = result.FailureReason ?? "重试开机启动设置未生效";
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

            var general = e.NewSettings.General;

            // 未编辑字段跟随最新当前值；已编辑字段保留草稿与原基线以支持冲突检测
            if (!_themeDirty)
            {
                _baseTheme = general.Theme;
                _targetTheme = general.Theme;
                OnPropertyChanged(nameof(Theme));
            }

            if (!_launchAtStartupDirty)
            {
                _baseLaunchAtStartup = general.LaunchAtStartup;
                _targetLaunchAtStartup = general.LaunchAtStartup;
                OnPropertyChanged(nameof(LaunchAtStartup));
            }

            if (!_showStartupNotificationDirty)
            {
                _baseShowStartupNotification = general.ShowStartupNotification;
                _targetShowStartupNotification = general.ShowStartupNotification;
                OnPropertyChanged(nameof(ShowStartupNotification));
            }

            if (!_logLevelDirty)
            {
                _baseLogLevel = general.LogLevel;
                _targetLogLevel = general.LogLevel;
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
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
