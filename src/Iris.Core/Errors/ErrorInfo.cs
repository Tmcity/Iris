namespace Iris.Core.Errors;

/// <summary>
/// 可序列化、可展示的错误描述。Message 为面向用户的中文短句；Detail 为技术细节（仅日志 / 展开查看）。
/// Detail 中严禁出现 API Key、Authorization 头等敏感信息。
/// </summary>
public sealed record ErrorInfo(
    ErrorCode Code,
    string Message,
    string? Detail = null,
    int? HttpStatus = null,
    bool Retryable = false,
    string? ProviderId = null);
