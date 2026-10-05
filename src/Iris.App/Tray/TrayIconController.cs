using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Iris.App.Settings;
using Iris.Core.Actions;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.App.Tray;

/// <summary>
/// 管理系统托盘图标、右键菜单及通知展示。
/// </summary>
internal sealed partial class TrayIconController : IDisposable
{
    private readonly IAppLifetime _appLifetime;
    private readonly ISettingsService _settingsService;
    private readonly Iris.Core.Actions.IActionDispatcher _actionDispatcher;
    private readonly ILogger<TrayIconController> _logger;
    private readonly ITrayNotificationNative? _notificationNative;
    private readonly AsyncRelayCommand _leftClickCommand;

    private TaskbarIcon? _taskbarIcon;
    private ImageSource? _iconSource;
    private DispatcherTimer? _retryTimer;
    private int _attemptCount;
    private bool _isCreated;
    private bool _disposed;

    public bool IsCreated => _isCreated;
    internal MenuItem? InputTranslateMenuItem { get; private set; }
    internal MenuItem? ClipboardTranslateMenuItem { get; private set; }
    internal MenuItem? SettingsMenuItem { get; private set; }
    internal ICommand LeftClickCommand => _leftClickCommand;
    internal Task? LastInputTranslateTask { get; private set; }
    internal Task? LastClipboardTranslateTask { get; private set; }
    internal Task? LastOpenSettingsTask { get; private set; }

    public event EventHandler? Created;

    public TrayIconController(
        IAppLifetime appLifetime,
        ISettingsService settingsService,
        Iris.Core.Actions.IActionDispatcher actionDispatcher,
        ILogger<TrayIconController> logger,
        ITrayNotificationNative? notificationNative = null)
    {
        _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _actionDispatcher = actionDispatcher ?? throw new ArgumentNullException(nameof(actionDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationNative = notificationNative;

        _leftClickCommand = new AsyncRelayCommand(
            DispatchInputTranslateAsync,
            () => !_disposed,
            ex => LogInputTranslateFailed(_logger, ex.GetType().Name));
    }

    internal bool WasInitialized { get; private set; }
    internal bool SuppressIconCreationForTesting { get; set; }

    public void Initialize()
    {
        WasInitialized = true;
        if (_taskbarIcon != null || _disposed || SuppressIconCreationForTesting)
        {
            return;
        }

        var iconUri = new Uri("pack://application:,,,/Iris.App;component/Resources/Icons/iris.ico", UriKind.Absolute);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = iconUri;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        _iconSource = bitmap;
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = UiText.TrayToolTip,
            IconSource = _iconSource,
            ContextMenu = CreateContextMenu(),
            LeftClickCommand = _leftClickCommand,
        };

        TryCreate();
    }

    private void TryCreate()
    {
        if (_disposed || _isCreated || _taskbarIcon == null)
        {
            return;
        }

        _attemptCount++;

        try
        {
#if DEBUG
            CheckSimulateCreationFailure();
#endif
            _taskbarIcon.ForceCreate(false);
            _isCreated = true;
            StopRetryTimer();
            LogCreatedSuccess(_logger, _attemptCount);
            Created?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex)
        {
            var delay = TrayCreationRetryPolicy.GetDelay(_attemptCount);

            if (_attemptCount == 1)
            {
                LogFirstAttemptFailed(_logger, ex, delay.TotalSeconds, _attemptCount);
            }
            else if (_attemptCount == TrayCreationRetryPolicy.ErrorLogThreshold)
            {
                LogErrorLogThresholdReached(_logger, ex, TrayCreationRetryPolicy.ErrorLogThreshold);
            }

            ScheduleRetry(delay);
        }
    }

#if DEBUG
    private int _simulatedFailures;

    /// <summary>
    /// 仅用于 DEBUG 测试（如手动验收 AC-4）：
    /// 当设置了环境变量 IRIS_DEBUG_TRAY_FAIL_ATTEMPTS=n 时，在前 n 次尝试创建托盘图标时人为抛出异常，
    /// 模拟系统启动初期通知区域未就绪的场景。
    /// </summary>
    private void CheckSimulateCreationFailure()
    {
        var env = Environment.GetEnvironmentVariable("IRIS_DEBUG_TRAY_FAIL_ATTEMPTS");
        if (int.TryParse(env, out var targetFails) && targetFails > 0)
        {
            if (_simulatedFailures < targetFails)
            {
                _simulatedFailures++;
                throw new InvalidOperationException($"[DEBUG] 模拟托盘创建失败（第 {_simulatedFailures}/{targetFails} 次尝试）");
            }
        }
    }
#endif

    private void ScheduleRetry(TimeSpan delay)
    {
        if (_disposed || _isCreated)
        {
            return;
        }

        _retryTimer ??= new DispatcherTimer();
        _retryTimer.Interval = delay;
        _retryTimer.Tick -= OnRetryTimerTick;
        _retryTimer.Tick += OnRetryTimerTick;
        _retryTimer.Start();
    }

    private void OnRetryTimerTick(object? sender, EventArgs e)
    {
        _retryTimer?.Stop();
        TryCreate();
    }

    private void StopRetryTimer()
    {
        if (_retryTimer != null)
        {
            _retryTimer.Stop();
            _retryTimer.Tick -= OnRetryTimerTick;
            _retryTimer = null;
        }
    }

    public void ShowNotification(string title, string message, NotificationLevel level)
    {
        if (_taskbarIcon == null || _disposed || !_isCreated)
        {
            return;
        }

        var icon = level switch
        {
            NotificationLevel.Info or NotificationLevel.Success => NotificationIcon.Info,
            NotificationLevel.Warning => NotificationIcon.Warning,
            NotificationLevel.Error => NotificationIcon.Error,
            _ => NotificationIcon.Info,
        };

        try
        {
            _taskbarIcon.ShowNotification(title, message, icon);
        }
        catch (Exception ex)
        {
            LogShowNotificationFailed(_logger, ex);
        }
    }

    internal bool TryShowNotificationNow(string title, string message, NotificationLevel level)
    {
        if (_disposed) return false;
        try
        {
            if (_notificationNative != null)
            {
                if (!_notificationNative.IsCreated) return false;
                _notificationNative.Show(title, message, level);
            }
            else
            {
                if (_taskbarIcon == null || !_isCreated) return false;
                var icon = level switch
                {
                    NotificationLevel.Warning => NotificationIcon.Warning,
                    NotificationLevel.Error => NotificationIcon.Error,
                    _ => NotificationIcon.Info
                };
                _taskbarIcon.ShowNotification(title, message, icon);
            }
            return true;
        }
        catch (Exception ex)
        {
            LogImmediateNotificationFailed(_logger, ex.GetType().Name);
            return false;
        }
    }

    private ContextMenu CreateContextMenu()
    {
        var contextMenu = new ContextMenu();

        var inputTranslateItem = new MenuItem
        {
            Header = UiText.TrayMenuInputTranslate,
            IsEnabled = true,
        };
        inputTranslateItem.Click += OnInputTranslateClick;
        InputTranslateMenuItem = inputTranslateItem;
        contextMenu.Items.Add(inputTranslateItem);

        var clipboardTranslateItem = new MenuItem
        {
            Header = UiText.TrayMenuClipboardTranslate,
            IsEnabled = true,
        };
        clipboardTranslateItem.Click += OnClipboardTranslateClick;
        ClipboardTranslateMenuItem = clipboardTranslateItem;
        contextMenu.Items.Add(clipboardTranslateItem);

        contextMenu.Items.Add(new Separator());

        var settingsItem = new MenuItem
        {
            Header = UiText.TrayMenuSettings,
            IsEnabled = true,
        };
        settingsItem.Click += OnSettingsClick;
        SettingsMenuItem = settingsItem;
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new Separator());

#if DEBUG
        var debugUiExceptionItem = new MenuItem
        {
            Header = UiText.TrayMenuDebugUiException,
        };
        debugUiExceptionItem.Click += (_, _) =>
        {
            throw new InvalidOperationException("Debug UI thread unhandled exception");
        };
        contextMenu.Items.Add(debugUiExceptionItem);

        var debugTaskExceptionItem = new MenuItem
        {
            Header = UiText.TrayMenuDebugTaskException,
        };
        debugTaskExceptionItem.Click += async (_, _) =>
        {
            _ = Task.Run(() => throw new InvalidOperationException("Debug background task unhandled exception"));
            await Task.Delay(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        };
        contextMenu.Items.Add(debugTaskExceptionItem);

        var debugToggleThemeItem = new MenuItem
        {
            Header = UiText.TrayMenuDebugToggleTheme,
        };
        debugToggleThemeItem.Click += async (_, _) =>
        {
            await _settingsService.UpdateAsync(s =>
            {
                var nextTheme = s.General.Theme switch
                {
                    AppTheme.System => AppTheme.Light,
                    AppTheme.Light => AppTheme.Dark,
                    AppTheme.Dark => AppTheme.System,
                    _ => AppTheme.System,
                };
                return s with { General = s.General with { Theme = nextTheme } };
            });
        };
        contextMenu.Items.Add(debugToggleThemeItem);

        contextMenu.Items.Add(new Separator());
#endif

        var exitItem = new MenuItem
        {
            Header = UiText.TrayMenuExit,
        };
        exitItem.Click += (_, _) =>
        {
            _appLifetime.RequestShutdown();
        };
        contextMenu.Items.Add(exitItem);

        return contextMenu;
    }

    private async void OnInputTranslateClick(object? sender, RoutedEventArgs e)
    {
        await DispatchInputTranslateAsync();
    }

    private async void OnClipboardTranslateClick(object? sender, RoutedEventArgs e)
    {
        await DispatchClipboardTranslateAsync();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        await DispatchOpenSettingsAsync();
    }

    internal Task DispatchInputTranslateAsync()
    {
        var task = ExecuteCoreAsync();
        LastInputTranslateTask = task;
        return task;

        async Task ExecuteCoreAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _actionDispatcher.DispatchAsync(
                    IrisAction.InputTranslate,
                    new ActionContext(ActionTrigger.TrayMenu)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogInputTranslateFailed(_logger, ex.GetType().Name);
            }
        }
    }

    internal Task DispatchClipboardTranslateAsync()
    {
        var task = ExecuteCoreAsync();
        LastClipboardTranslateTask = task;
        return task;

        async Task ExecuteCoreAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _actionDispatcher.DispatchAsync(
                    IrisAction.ClipboardTranslate,
                    new ActionContext(ActionTrigger.TrayMenu)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogClipboardTranslateFailed(_logger, ex.GetType().Name);
            }
        }
    }

    internal Task DispatchOpenSettingsAsync()
    {
        var task = ExecuteCoreAsync();
        LastOpenSettingsTask = task;
        return task;

        async Task ExecuteCoreAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _actionDispatcher.DispatchAsync(
                    IrisAction.OpenSettings,
                    new ActionContext(ActionTrigger.TrayMenu)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogOpenSettingsFailed(_logger, ex.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRetryTimer();

        if (InputTranslateMenuItem != null)
        {
            InputTranslateMenuItem.Click -= OnInputTranslateClick;
        }
        if (ClipboardTranslateMenuItem != null)
        {
            ClipboardTranslateMenuItem.Click -= OnClipboardTranslateClick;
        }
        if (SettingsMenuItem != null)
        {
            SettingsMenuItem.Click -= OnSettingsClick;
        }

        if (_taskbarIcon != null)
        {
            _taskbarIcon.LeftClickCommand = null;
            _taskbarIcon.ContextMenu = null;
            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }

        _leftClickCommand.RaiseCanExecuteChanged();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "系统托盘图标创建失败，将在 {DelaySeconds:F0} 秒后重试（第 {Attempt} 次尝试）")]
    private static partial void LogFirstAttemptFailed(ILogger logger, Exception ex, double delaySeconds, int attempt);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "系统托盘图标连续创建失败达到 {Threshold} 次，系统仍将继续重试")]
    private static partial void LogErrorLogThresholdReached(ILogger logger, Exception ex, int threshold);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "系统托盘图标创建成功（共尝试 {Attempts} 次）")]
    private static partial void LogCreatedSuccess(ILogger logger, int attempts);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "展示托盘通知失败")]
    private static partial void LogShowNotificationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "即时托盘通知失败: {ExceptionType}")]
    private static partial void LogImmediateNotificationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "托盘派发打开设置动作失败：{ErrorType}")]
    private static partial void LogOpenSettingsFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "托盘派发输入翻译动作失败：{ErrorType}")]
    private static partial void LogInputTranslateFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "托盘派发剪贴板翻译动作失败：{ErrorType}")]
    private static partial void LogClipboardTranslateFailed(ILogger logger, string errorType);
}
