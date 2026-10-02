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

    // 翻译服务页与凭据
    public const string SettingsNavTranslation = "翻译服务";
    public const string SettingsAddProfile = "添加服务";
    public const string SettingsDeleteProfile = "删除服务";
    public const string SettingsMoveUp = "上移";
    public const string SettingsMoveDown = "下移";
    public const string SettingsProfileName = "服务名称";
    public const string SettingsProfileKind = "协议类型";
    public const string SettingsProfileBaseUrl = "API 地址";
    public const string SettingsProfileModel = "模型名称";
    public const string SettingsProfileTimeout = "超时时间（秒）";
    public const string SettingsProfileMaxTokens = "最大输出 Tokens";
    public const string SettingsProfileTemperature = "采样温度";
    public const string SettingsProfilePrompt = "Prompt 模板";
    public const string SettingsProfilePromptDefault = "跟随默认模板";
    public const string SettingsProfileApiKey = "API Key";
    public const string SettingsProfileAdvanced = "高级选项";
    public const string SettingsProfileEnabled = "已启用";
    public const string SettingsCredKeep = "保持现有凭据（如有）";
    public const string SettingsCredReplace = "替换 API Key";
    public const string SettingsCredClear = "保存后清除凭据";
    public const string SettingsTestDraft = "测试连接";
    public const string SettingsListDraftModels = "获取模型列表";
    public const string SettingsTestingDraft = "正在测试连接…";
    public const string SettingsListingModels = "正在获取模型列表…";
    public const string SettingsTestDraftSuccess = "连接测试成功";
    public const string SettingsTestDraftFailed = "连接测试失败";
    public const string SettingsListModelsFailed = "获取模型列表失败";
    public const string SettingsHaikuEffortWarning = "Haiku 模型不支持 Effort，已自动清空";
    public const string SettingsAnthropicV1Warning = "官方 SDK 已自动包含 /v1 路径，反代地址通常不需要以 /v1 结尾";
    public const string SettingsExtraHeaders = "附加请求头 (格式: Name: Value 每行一个)";
    public const string SettingsChatCompletionsPath = "Completions 相对路径";
    public const string SettingsStream = "流式响应 (Stream)";
    public const string SettingsIncludeStreamUsage = "流式 Token 用量统计 (IncludeStreamUsage)";
    public const string SettingsStripThinkTags = "移除思考标记 (<think> tags)";
    public const string SettingsRefusalFallback = "服务端拒答回退";
    public const string SettingsEffort = "推理强度 (Effort)";
    public const string SettingsMaxTokensField = "Token 上限字段";

    // 进程级待应用状态与重试
    public const string SettingsRetryPendingApply = "重试服务应用";
    public const string SettingsStatusPendingApplyQuotaFull = "待应用的服务清理事项过多，请先重试未完成的待应用事项";
    public const string SettingsStatusSavedPendingApply = "设置已保存，但翻译服务刷新/清理未完成（可重试）";
    public const string SettingsStatusSavedPendingApplyAndAutoStart = "设置已保存，但服务刷新与开机启动均未完成（可重试）";
    public const string SettingsStatusRetryingPendingApply = "正在重试翻译服务应用…";
    public const string SettingsStatusPendingApplyCompleted = "翻译服务应用已完成";
    public const string SettingsStatusPendingApplyFailed = "重试翻译服务应用未完成";

    // 翻译窗口
    public const string TranslateWindowTitle = "Iris 翻译";
    public const string TranslatePinWindow = "钉住窗口";
    public const string TranslateUnpinWindow = "取消钉住";
    public const string TranslateSettings = "设置";
    public const string TranslateClose = "关闭";
    public const string TranslateInputPlaceholder = "输入文本，按 Enter 翻译…";
    public const string TranslateButton = "翻译";
    public const string TranslateStopButton = "停止";
    public const string TranslateSwapLanguages = "交换语言";
    public const string TranslateCopy = "复制";
    public const string TranslateCopyVisible = "复制可见内容";
    public const string TranslateRetry = "重试";
    public const string TranslateGoToSettings = "去设置";
    public const string TranslateLocalTruncated = "本地显示已截断";
    public const string TranslateModelTruncated = "结果不完整 (模型截断)";
    public const string TranslateFromCache = "缓存";
    public const string TranslateEmptyPrompt = "请输入要翻译的文本";
    public const string TranslateStopped = "翻译已停止";
    public const string TranslateLastResult = "上次结果";
    public const string TranslateBusy = "系统忙碌，请稍后再试";
    public const string TranslateNoConfiguredProviders = "未配置任何可用的翻译服务，请前往设置配置。";
    public const string TranslateInputTooLong = "输入文本超出最大字符限制";
}
