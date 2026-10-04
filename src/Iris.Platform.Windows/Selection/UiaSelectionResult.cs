using System;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// UIA 选区读取结果模型（内部不可变模型）。
/// 仅在 Success 时携带非空白文本；其他状态 Text 一律为 null。
/// ToString / 日志仅包含状态、阶段和安全错误码，绝不暴露选区文本、窗口标题、协议正文或原始异常。
/// </summary>
internal sealed record UiaSelectionResult
{
    public UiaSelectionStatus Status { get; }
    public string? Text { get; }
    public string? ErrorCode { get; }
    public string RequestId { get; }

    public UiaSelectionResult(
        UiaSelectionStatus status,
        string? text,
        string? errorCode,
        string requestId)
    {
        Status = status;
        Text = status == UiaSelectionStatus.Success ? text : null;
        ErrorCode = SanitizeErrorCode(errorCode);
        RequestId = requestId ?? string.Empty;
    }

    public static string? SanitizeErrorCode(string? rawCode)
    {
        if (string.IsNullOrEmpty(rawCode)) return null;
        return UiaProtocolCodec.IsValidErrorCode(rawCode) ? rawCode : "INVALID_ERROR_CODE";
    }

    public static UiaSelectionResult CreateSuccess(string requestId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new UiaSelectionResult(UiaSelectionStatus.Empty, null, null, requestId);
        }

        return new UiaSelectionResult(UiaSelectionStatus.Success, text, null, requestId);
    }

    public static UiaSelectionResult CreateEmpty(string requestId) =>
        new(UiaSelectionStatus.Empty, null, null, requestId);

    public static UiaSelectionResult CreateUnsupported(string requestId, string? errorCode = "UNSUPPORTED") =>
        new(UiaSelectionStatus.Unsupported, null, errorCode, requestId);

    public static UiaSelectionResult CreateSensitive(string requestId) =>
        new(UiaSelectionStatus.Sensitive, null, "PASSWORD_OR_SENSITIVE", requestId);

    public static UiaSelectionResult CreateTooLong(string requestId, string? errorCode = "TOO_LONG") =>
        new(UiaSelectionStatus.TooLong, null, errorCode, requestId);

    public static UiaSelectionResult CreateTimedOut(string requestId) =>
        new(UiaSelectionStatus.TimedOut, null, "TIMED_OUT", requestId);

    public static UiaSelectionResult CreateTargetChanged(string requestId) =>
        new(UiaSelectionStatus.TargetChanged, null, "TARGET_CHANGED", requestId);

    public static UiaSelectionResult CreateBusy(string requestId) =>
        new(UiaSelectionStatus.Busy, null, "BUSY", requestId);

    public static UiaSelectionResult CreateFailed(string requestId, string? errorCode) =>
        new(UiaSelectionStatus.Failed, null, errorCode ?? "FAILED", requestId);

    public override string ToString() =>
        $"UiaSelectionResult(Status={Status}, RequestId={RequestId}, ErrorCode={ErrorCode ?? "None"})";
}
