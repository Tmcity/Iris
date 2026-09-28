namespace Iris.Core.Errors;

/// <summary>
/// Iris 内所有“已知失败”都以此异常（或子类）抛出。未知异常在边界处（Orchestrator / ActionDispatcher）
/// 统一包装为 ErrorCode.Unknown。用户主动取消使用 OperationCanceledException，不使用本异常。
/// </summary>
public class IrisException : Exception
{
    public IrisException(
        ErrorCode code,
        string message,
        Exception? innerException = null,
        int? httpStatus = null,
        bool retryable = false,
        string? providerId = null,
        string? detail = null)
        : base(message, innerException)
    {
        Code = code;
        HttpStatus = httpStatus;
        Retryable = retryable;
        ProviderId = providerId;
        Detail = detail;
    }

    public ErrorCode Code { get; }
    public int? HttpStatus { get; }
    public bool Retryable { get; }
    public string? ProviderId { get; }
    public string? Detail { get; }

    public ErrorInfo ToErrorInfo() =>
        new(Code, Message, Detail, HttpStatus, Retryable, ProviderId);
}
