namespace Iris.Core.Errors;

/// <summary>
/// 统一错误码。数值分段：1xxx 通用 / 2xxx 远程服务 / 3xxx OCR / 4xxx 平台 / 5xxx 存储。
/// 数值会写入日志与历史，一经发布不得修改或复用。
/// </summary>
public enum ErrorCode
{
    Unknown = 0,

    // ---- 1xxx 通用 ----
    Cancelled = 1001,
    Timeout = 1002,
    NetworkUnavailable = 1003,
    InvalidArgument = 1004,
    /// <summary>服务未配置：缺少 Key、没有启用的服务、或指定的服务不存在。</summary>
    NotConfigured = 1005,
    InputTooLong = 1006,
    InputEmpty = 1007,

    // ---- 2xxx 远程服务（翻译 / OCR 共用）----
    /// <summary>401 / Key 无效。</summary>
    AuthenticationFailed = 2001,
    /// <summary>403。</summary>
    PermissionDenied = 2002,
    /// <summary>429 / QPS 超限，可重试。</summary>
    RateLimited = 2003,
    /// <summary>余额不足 / 配额用尽（含 OpenAI 的 insufficient_quota、DeepSeek 的 402、百度的 17/19）。</summary>
    QuotaExceeded = 2004,
    /// <summary>5xx / overloaded。</summary>
    ProviderUnavailable = 2005,
    /// <summary>400 / 422。</summary>
    BadRequest = 2006,
    /// <summary>404：BaseUrl 或模型名错误。</summary>
    EndpointNotFound = 2007,
    /// <summary>响应无法解析或不符合协议。</summary>
    InvalidResponse = 2008,
    /// <summary>模型拒答或内容被过滤（Anthropic stop_reason=refusal、OpenAI finish_reason=content_filter）。</summary>
    ContentRefused = 2009,
    /// <summary>输出达到 max_tokens 被截断（通常只作为警告，见 ProviderCompleted.Truncated）。</summary>
    ContentTruncated = 2010,

    // ---- 3xxx OCR ----
    OcrNoTextFound = 3001,
    ImageInvalid = 3002,
    ImageTooLarge = 3003,
    OcrLanguageUnsupported = 3004,

    // ---- 4xxx 平台 ----
    HotkeyConflict = 4001,
    HotkeyInvalid = 4002,
    SelectionEmpty = 4003,
    ClipboardBusy = 4004,
    CaptureFailed = 4005,
    HookInstallFailed = 4006,

    // ---- 5xxx 存储 ----
    SettingsCorrupted = 5001,
    SecretUnavailable = 5002,
    StorageFailure = 5003,
}
