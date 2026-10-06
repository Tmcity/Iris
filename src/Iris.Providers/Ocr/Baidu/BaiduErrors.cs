using Iris.Core.Errors;
using Iris.Providers.Http;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Iris.Providers.Ocr.Baidu;

internal sealed class BaiduException(ErrorCode code, string stage, string? providerId, int? http = null, int? business = null, bool retryable = false, string? diagnostics = null)
    : IrisException(code, ErrorMessages.GetDefault(code), httpStatus: http, retryable: retryable, providerId: providerId,
        detail: $"stage={stage};httpStatus={http};businessCode={business};{diagnostics}");

internal static class BaiduErrors
{
    internal static string Describe(string stage, Exception ex) => ex switch
    {
        BaiduException known => $"{stage}[{known.Detail}]",
        AggregateException aggregate => $"{stage}[{string.Join(';', aggregate.InnerExceptions.Select(e => Describe("ERROR", e)))}]",
        BaiduTransportFailure transport => $"{stage}[{transport.Diagnostic}]",
        _ => $"stage={stage};hr=0x{ex.HResult:X8}"
    };
    internal static BaiduException External(string stage, Exception ex, string id) => ex as BaiduException ??
        new(ex switch
        {
            TimeoutRejectedException => ErrorCode.Timeout, BrokenCircuitException => ErrorCode.ProviderUnavailable,
            HttpRequestException or IOException => ErrorCode.NetworkUnavailable,
            System.Text.Json.JsonException => ErrorCode.InvalidResponse, _ => ErrorCode.Unknown
        }, stage, id, retryable: ex is TimeoutRejectedException or BrokenCircuitException or HttpRequestException or IOException, diagnostics: Describe(stage, ex));
    internal static BaiduException Http(int status, string id, bool token)
    {
        var mapped = HttpErrorMapper.MapStatus(status, null);
        return new(!token && status == 413 ? ErrorCode.ImageTooLarge : mapped.Code, "HTTP", id, status, retryable: mapped.Retryable);
    }
    internal static BaiduException Business(int code, string id)
    {
        var mapped = code switch
        {
            110 or 111 or 14 => ErrorCode.AuthenticationFailed, 6 => ErrorCode.PermissionDenied, 17 or 19 => ErrorCode.QuotaExceeded,
            18 => ErrorCode.RateLimited, 216200 or 216201 => ErrorCode.ImageInvalid, 216202 or 216205 => ErrorCode.ImageTooLarge,
            216100 or 216101 => ErrorCode.BadRequest, 216630 or 282000 => ErrorCode.ProviderUnavailable, _ => ErrorCode.Unknown
        };
        return new(mapped, "BUSINESS", id, business: code, retryable: code is 18 or 216630 or 282000);
    }
}
