using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Iris.Core.Presentation;
using Iris.Core.Settings;

namespace Iris.App.Tray;

/// <summary>
/// 管理系统托盘图标、右键菜单及通知展示。
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly IAppLifetime _appLifetime;
    private readonly ISettingsService _settingsService;
    private TaskbarIcon? _taskbarIcon;
    private bool _disposed;

    public TrayIconController(IAppLifetime _appLifetime, ISettingsService settingsService)
    {
        this._appLifetime = _appLifetime ?? throw new ArgumentNullException(nameof(_appLifetime));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public void Initialize()
    {
        if (_taskbarIcon != null || _disposed)
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

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = UiText.TrayToolTip,
            IconSource = bitmap,
        };

        var streamInfo = Application.GetResourceStream(iconUri);
        if (streamInfo != null)
        {
            using var stream = streamInfo.Stream;
            _taskbarIcon.Icon = new System.Drawing.Icon(stream);
        }

        _taskbarIcon.ContextMenu = CreateContextMenu();
        try
        {
            _taskbarIcon.ForceCreate(false);
        }
        catch (InvalidOperationException)
        {
            // 当 Shell 托盘尚未就绪或在非活动桌面环境（如锁屏、自动化会话）中，Shell_NotifyIcon 返回 E_FAIL。
            // H.NotifyIcon 注册了 TaskbarCreated 消息，在 Explorer/托盘可用时会自动创建。
        }
    }

    public void ShowNotification(string title, string message, NotificationLevel level)
    {
        if (_taskbarIcon == null || _disposed)
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
        catch (InvalidOperationException)
        {
            // 托盘若因环境原因未能创建，静默跳过通知
        }
    }

    private ContextMenu CreateContextMenu()
    {
        var contextMenu = new ContextMenu();

        var settingsItem = new MenuItem
        {
            Header = UiText.TrayMenuSettings,
            IsEnabled = false, // TODO(T-018): 设置窗口尚未实现
        };
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
        debugTaskExceptionItem.Click += (_, _) =>
        {
            _ = Task.Run(() => throw new InvalidOperationException("Debug background task unhandled exception"));
            Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }
}
