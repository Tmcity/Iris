namespace Iris.App;

internal static class UiText
{
    public const string AppName = "Iris";
    public const string TrayToolTip = "Iris";
    public const string TrayMenuSettings = "设置…";
    public const string TrayMenuDebugUiException = "调试：UI 线程异常";
    public const string TrayMenuDebugTaskException = "调试：后台任务异常";
    public const string TrayMenuDebugToggleTheme = "调试：切换主题";
    public const string TrayMenuExit = "退出";

    public const string StartupRunningInBackground = "Iris 已在后台运行";
    public const string SettingsCorruptedNotice = "设置文件已损坏，已重置为默认值并将原文件备份。";
    public const string FatalUnknown = "发生未知严重错误，应用程序即将退出：";
    public const string DispatcherUnhandledMessage = "发生未知错误，详情请查看日志";
    public const string ActionNotAvailable = "该功能尚未开放";

    public const string SettingsWindowTitle = "Iris 设置";
    public const string SettingsTitle = "设置";
    public const string SettingsNavGeneral = "常规";
    public const string SettingsThemeGroup = "外观与主题";
    public const string SettingsThemeDescription = "选择应用程序的色彩主题模式。";
    public const string SettingsThemeSystem = "跟随系统";
    public const string SettingsThemeLight = "浅色";
    public const string SettingsThemeDark = "深色";
    public const string SettingsStartupGroup = "开机自启与通知";
    public const string SettingsLaunchAtStartup = "开机自动启动 Iris";
    public const string SettingsShowStartupNotification = "启动时显示后台运行通知";
    public const string SettingsLogGroup = "日志";
    public const string SettingsLogLevel = "日志级别";
    public const string SettingsLogDescription = "控制应用程序运行时输出的最小日志记录级别。";
    public const string SettingsSave = "保存";
    public const string SettingsCancel = "取消";
    public const string SettingsReload = "重新载入当前设置";
    public const string SettingsRetry = "重试应用";

    public const string SettingsStatusSaving = "正在保存…";
    public const string SettingsStatusSaved = "设置已保存";
    public const string SettingsStatusSavedAutoStartPending = "已保存，但开机启动设置未生效（可重试）";
    public const string SettingsStatusSavedAutoStartStillPending = "设置已保存，但开机启动设置仍未生效（可重试）";
    public const string SettingsStatusSaveFailed = "保存失败";
    public const string SettingsStatusRetryingAutoStart = "正在重试开机启动设置…";
    public const string SettingsStatusAutoStartApplied = "开机启动设置已生效";
    public const string SettingsStatusAutoStartRetryFailed = "重试开机启动设置未生效";
    public const string SettingsStatusConflict = "保存冲突：设置已被其他操作修改，请重新载入后再次编辑";
    public const string SettingsStatusStopped = "保存已停止";
    public const string SettingsStatusCanceled = "保存已取消";
    public const string SettingsStatusFileError = "保存设置文件失败";
    public const string SettingsStatusInvalidTheme = "无效的主题设置";
    public const string SettingsStatusInvalidLogLevel = "无效的日志级别设置";
}
