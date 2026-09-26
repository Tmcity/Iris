using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;

namespace Iris.Providers.Http;

/// <summary>
/// HTTP 状态码、响应体与网络异常到 IrisException 的映射。
/// </summary>
internal static partial class HttpErrorMapper
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex ConsecutiveWhitespaceRegex();

    public static (ErrorCode Code, bool Retryable) MapStatus(int statusCode, string? providerErrorCode)
    {
        if (statusCode is >= 300 and <= 399)
        {
            return (ErrorCode.InvalidResponse, false);
        }

        if (statusCode == 429)
        {
            if (string.Equals(providerErrorCode, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
            {
                return (ErrorCode.QuotaExceeded, false);
            }
            return (ErrorCode.RateLimited, true);
        }

        if (statusCode is 408 or 504)
        {
            return (ErrorCode.Timeout, true);
        }

        if (statusCode is 400 or 422)
        {
            return (ErrorCode.BadRequest, false);
        }

        if (statusCode == 401)
        {
            return (ErrorCode.AuthenticationFailed, false);
        }

        if (statusCode == 402)
        {
            return (ErrorCode.QuotaExceeded, false);
        }

        if (statusCode == 403)
        {
            return (ErrorCode.PermissionDenied, false);
        }

        if (statusCode == 404)
        {
            return (ErrorCode.EndpointNotFound, false);
        }

        if (statusCode == 413)
        {
            return (ErrorCode.InputTooLong, false);
        }

        if (statusCode is 500 or 502 or 503 or 529)
        {
            return (ErrorCode.ProviderUnavailable, true);
        }

        if (statusCode is >= 400 and <= 499)
        {
            return (ErrorCode.BadRequest, false);
        }

        if (statusCode is >= 500 and <= 599)
        {
            return (ErrorCode.ProviderUnavailable, true);
        }

        if (string.Equals(providerErrorCode, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
        {
            return (ErrorCode.QuotaExceeded, false);
        }

        return (ErrorCode.InvalidResponse, false);
    }

    /// <summary>纯函数：按 ARCH §11.2 从响应体中提取信息、脱敏、截断，构造异常。T-009 处理“200 响应中携带错误体”时也使用它。</summary>
    public static IrisException FromBody(int statusCode, string? body, string providerId, IReadOnlyList<string?>? knownSecrets)
    {
        var (extractedText, providerErrorCode) = ExtractErrorInfo(body);

        var redacted = SecretRedactor.Redact(extractedText, knownSecrets);
        var truncated = TruncateRunes(redacted, 300);

        var (code, retryable) = MapStatus(statusCode, providerErrorCode);
        var message = ErrorMessages.GetDefault(code);
        var detail = string.IsNullOrWhiteSpace(truncated)
            ? $"HTTP {statusCode}"
            : $"HTTP {statusCode}: {truncated}";

        return new IrisException(
            code,
            message,
            httpStatus: statusCode,
            retryable: retryable,
            providerId: providerId,
            detail: detail);
    }

    /// <summary>读取响应体的前 64 KB，然后调用 FromBody。</summary>
    public static async Task<IrisException> FromResponseAsync(
        HttpResponseMessage response,
        string providerId,
        IReadOnlyList<string?>? knownSecrets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        string? body = null;
        if (response.Content != null)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            const int maxBytes = 64 * 1024;
            var buffer = new byte[maxBytes];
            var totalRead = 0;
            while (totalRead < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, maxBytes - totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }
            body = Encoding.UTF8.GetString(buffer, 0, totalRead);
        }

        return FromBody((int)response.StatusCode, body, providerId, knownSecrets);
    }

    /// <summary>按 ARCH §11.2 的异常映射表转换；不应该被映射的异常（用户取消、编程错误）返回 null。</summary>
    public static IrisException? TryMapException(Exception exception, string providerId)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return null;
        }

        if (exception is HttpRequestException or IOException)
        {
            return new IrisException(
                ErrorCode.NetworkUnavailable,
                ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable),
                innerException: exception,
                retryable: true,
                providerId: providerId,
                detail: exception.Message);
        }

        if (exception is JsonException)
        {
            return new IrisException(
                ErrorCode.InvalidResponse,
                ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                innerException: exception,
                retryable: false,
                providerId: providerId,
                detail: exception.Message);
        }

        return null;
    }

    public static IrisException Timeout(string providerId, TimeSpan timeout)
    {
        return new IrisException(
            ErrorCode.Timeout,
            ErrorMessages.GetDefault(ErrorCode.Timeout),
            retryable: true,
            providerId: providerId,
            detail: $"请求超时（{timeout.TotalSeconds:G} 秒）");
    }

    private static (string ExtractedMessage, string? ProviderErrorCode) ExtractErrorInfo(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? message = null;
            string? code = null;
            string? type = null;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var errorElement))
                {
                    if (errorElement.ValueKind == JsonValueKind.Object)
                    {
                        if (errorElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        {
                            message = m.GetString();
                        }
                        if (errorElement.TryGetProperty("code", out var c))
                        {
                            code = c.ValueKind switch
                            {
                                JsonValueKind.String => c.GetString(),
                                JsonValueKind.Number => c.GetRawText(),
                                _ => null,
                            };
                        }
                        if (errorElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            type = t.GetString();
                        }
                    }
                    else if (errorElement.ValueKind == JsonValueKind.String)
                    {
                        message = errorElement.GetString();
                    }
                }

                if (string.IsNullOrEmpty(message) && root.TryGetProperty("message", out var rootMsg) && rootMsg.ValueKind == JsonValueKind.String)
                {
                    message = rootMsg.GetString();
                }

                if (string.IsNullOrEmpty(message) && root.TryGetProperty("detail", out var rootDetail) && rootDetail.ValueKind == JsonValueKind.String)
                {
                    message = rootDetail.GetString();
                }

                if (string.IsNullOrEmpty(message) && root.TryGetProperty("error_msg", out var rootErrorMsg) && rootErrorMsg.ValueKind == JsonValueKind.String)
                {
                    message = rootErrorMsg.GetString();
                }

                if (string.IsNullOrEmpty(code))
                {
                    if (root.TryGetProperty("code", out var rootCode))
                    {
                        code = rootCode.ValueKind switch
                        {
                            JsonValueKind.String => rootCode.GetString(),
                            JsonValueKind.Number => rootCode.GetRawText(),
                            _ => null,
                        };
                    }
                    else if (root.TryGetProperty("error_code", out var rootErrorCode))
                    {
                        code = rootErrorCode.ValueKind switch
                        {
                            JsonValueKind.String => rootErrorCode.GetString(),
                            JsonValueKind.Number => rootErrorCode.GetRawText(),
                            _ => null,
                        };
                    }
                }

                if (string.IsNullOrEmpty(type) && root.TryGetProperty("type", out var rootType) && rootType.ValueKind == JsonValueKind.String)
                {
                    type = rootType.GetString();
                }
            }

            string? providerErrorCode = null;
            if (string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
            {
                providerErrorCode = "insufficient_quota";
            }
            else
            {
                providerErrorCode = code ?? type;
            }

            if (!string.IsNullOrEmpty(message))
            {
                return (message, providerErrorCode);
            }

            var compressed = ConsecutiveWhitespaceRegex().Replace(body.Trim(), " ");
            return (compressed, providerErrorCode);
        }
        catch (JsonException)
        {
            var compressed = ConsecutiveWhitespaceRegex().Replace(body.Trim(), " ");
            return (compressed, null);
        }
    }

    private static string TruncateRunes(string text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var runeCount = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            runeCount++;
            if (runeCount > maxRunes)
            {
                break;
            }
        }

        if (runeCount <= maxRunes)
        {
            return text;
        }

        var sb = new StringBuilder(maxRunes * 2 + 1);
        var taken = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken >= maxRunes)
            {
                break;
            }
            sb.Append(rune);
            taken++;
        }
        sb.Append('…');
        return sb.ToString();
    }
}
