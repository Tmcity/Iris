namespace Iris.Core.Errors;

/// <summary>
/// 为各 ErrorCode 提供默认的面向用户的中文提示文案。
/// </summary>
public static class ErrorMessages
{
    /// <summary>
    /// 获取指定错误码的默认中文提示文案。未定义的错误码返回 Unknown 的默认文案。
    /// </summary>
    public static string GetDefault(ErrorCode code) => code switch
    {
        ErrorCode.Unknown => "发生未知错误，详情请查看日志",
        ErrorCode.Cancelled => "操作已取消",
        ErrorCode.Timeout => "请求超时，请检查网络或稍后重试",
        ErrorCode.NetworkUnavailable => "网络连接失败，请检查网络或代理设置",
        ErrorCode.InvalidArgument => "参数无效",
        ErrorCode.NotConfigured => "服务尚未配置，请先在设置中完成配置",
        ErrorCode.InputTooLong => "文本过长，请缩短后重试",
        ErrorCode.InputEmpty => "没有需要翻译的文本",
        ErrorCode.AuthenticationFailed => "API Key 无效或已过期，请检查设置",
        ErrorCode.PermissionDenied => "没有访问该服务的权限（可能未开通该接口）",
        ErrorCode.RateLimited => "请求过于频繁，请稍后重试",
        ErrorCode.QuotaExceeded => "余额或调用额度不足",
        ErrorCode.ProviderUnavailable => "服务暂时不可用，请稍后重试",
        ErrorCode.BadRequest => "请求被服务拒绝，请检查模型名称与参数",
        ErrorCode.EndpointNotFound => "服务地址或模型不存在，请检查 BaseUrl 与模型名称",
        ErrorCode.InvalidResponse => "服务返回了无法识别的响应",
        ErrorCode.ContentRefused => "服务拒绝处理该内容",
        ErrorCode.ContentTruncated => "结果过长已被截断，内容可能不完整",
        ErrorCode.OcrNoTextFound => "未识别到文字",
        ErrorCode.ImageInvalid => "图片无效或格式不受支持",
        ErrorCode.ImageTooLarge => "图片过大，请缩小截图范围",
        ErrorCode.OcrLanguageUnsupported => "系统未安装该语言的 OCR 组件",
        ErrorCode.HotkeyConflict => "快捷键已被其他程序占用",
        ErrorCode.HotkeyInvalid => "快捷键组合无效",
        ErrorCode.SelectionEmpty => "未检测到选中的文本",
        ErrorCode.ClipboardBusy => "剪贴板正被其他程序占用，请稍后重试",
        ErrorCode.CaptureFailed => "内容获取失败，请稍后重试",
        ErrorCode.HookInstallFailed => "鼠标监听启动失败，划词功能不可用",
        ErrorCode.SettingsCorrupted => "设置文件已损坏，已恢复为默认设置（原文件已备份）",
        ErrorCode.SecretUnavailable => "无法读取已保存的密钥，请重新填写",
        ErrorCode.StorageFailure => "读写本地数据失败",
        _ => "发生未知错误，详情请查看日志",
    };
}
