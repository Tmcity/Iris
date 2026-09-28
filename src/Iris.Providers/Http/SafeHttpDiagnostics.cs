using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iris.Providers.Http;

/// <summary>
/// 安全 HTTP 诊断与 URL 脱敏工具（ARCH v1.13 §11.2–§11.3、§12.5）。
/// 纯函数，仅提供安全的 URL 格式化与异常元数据，不暴露原始消息、查询参数、认证信息或内部异常链。
/// </summary>
internal static partial class SafeHttpDiagnostics
{
    public const string RedactedUrlPlaceholder = "<redacted-url>";

    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s""<>]+")]
    private static partial Regex AbsoluteUrlRegex();

    private static int CountOccurrences(string s, char c)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == c)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 将绝对 URI 格式化为 scheme + "://" + host + 非默认端口 + path；
    /// 完整去除 UserInfo（用户名/密码）、Query（查询参数）与 Fragment（锚点）。
    /// 非绝对 URI 或 null 返回固定占位符，不回显原输入。
    /// </summary>
    public static string ToSafeUrl(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return RedactedUrlPlaceholder;
        }

        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{portPart}{uri.AbsolutePath}";
    }

    /// <summary>
    /// 将字符串形式的绝对 URL 转换为安全 URL。无法解析为绝对 URI 的输入返回固定占位符。
    /// </summary>
    public static string ToSafeUrl(string? urlString)
    {
        if (string.IsNullOrWhiteSpace(urlString) || !Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
        {
            return RedactedUrlPlaceholder;
        }

        return ToSafeUrl(uri);
    }

    /// <summary>
    /// 清理文本中的所有绝对 URL，去除其中的 UserInfo、Query 与 Fragment。
    /// 无法解析为合法绝对 URI 的可疑 URL 返回固定占位符，绝不回显原文本。
    /// </summary>
    public static string CleanUrlsInText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return AbsoluteUrlRegex().Replace(text, match =>
        {
            var raw = match.Value;
            var candidate = raw;
            var suffix = string.Empty;
            var pre = match.Index > 0 ? text[match.Index - 1] : '\0';

            while (candidate.Length > 0)
            {
                var last = candidate[^1];
                if (last is '.' or ',' or ';' or ':')
                {
                    suffix = last + suffix;
                    candidate = candidate[..^1];
                }
                else if (last == ')' && CountOccurrences(candidate, ')') > CountOccurrences(candidate, '('))
                {
                    suffix = last + suffix;
                    candidate = candidate[..^1];
                }
                else if (last == ']' && CountOccurrences(candidate, ']') > CountOccurrences(candidate, '['))
                {
                    suffix = last + suffix;
                    candidate = candidate[..^1];
                }
                else if (last == '}' && CountOccurrences(candidate, '}') > CountOccurrences(candidate, '{'))
                {
                    suffix = last + suffix;
                    candidate = candidate[..^1];
                }
                else if (last == '\'' && (pre == '\'' || !candidate[..^1].Contains('\'')))
                {
                    suffix = last + suffix;
                    candidate = candidate[..^1];
                }
                else
                {
                    break;
                }
            }

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                return ToSafeUrl(uri) + suffix;
            }

            return RedactedUrlPlaceholder + suffix;
        });
    }

    /// <summary>
    /// 格式化 HttpRequestException 的安全 Detail，不读 Message、Data，仅保留安全元数据。
    /// </summary>
    public static string FormatHttpRequestExceptionDetail(HttpRequestException httpEx)
    {
        ArgumentNullException.ThrowIfNull(httpEx);
        var type = httpEx.GetType().Name;
        var parts = new List<string> { type };
        if (httpEx.HttpRequestError != HttpRequestError.Unknown)
        {
            parts.Add($"HttpRequestError={httpEx.HttpRequestError}");
        }
        if (httpEx.StatusCode.HasValue)
        {
            parts.Add($"Status={(int)httpEx.StatusCode.Value}");
        }
        parts.Add($"HResult=0x{httpEx.HResult:X8}");
        return $"网络请求失败（{string.Join("; ", parts)}）";
    }

    /// <summary>
    /// 格式化 IOException 或网络传输异常的安全 Detail，不读 Message、Data，仅保留安全元数据。
    /// </summary>
    public static string FormatIOExceptionDetail(Exception ioEx)
    {
        ArgumentNullException.ThrowIfNull(ioEx);
        var type = ioEx.GetType().Name;
        return $"网络传输读写失败（{type}; HResult=0x{ioEx.HResult:X8}）";
    }

    /// <summary>
    /// 格式化 JsonException 的安全 Detail，不读 Message、Path、Data，仅保留行号/位置等安全元数据。
    /// </summary>
    public static string FormatJsonExceptionDetail(JsonException jsonEx)
    {
        ArgumentNullException.ThrowIfNull(jsonEx);
        var type = jsonEx.GetType().Name;
        var parts = new List<string> { type };
        if (jsonEx.LineNumber.HasValue)
        {
            parts.Add($"LineNumber={jsonEx.LineNumber.Value}");
        }
        if (jsonEx.BytePositionInLine.HasValue)
        {
            parts.Add($"BytePosition={jsonEx.BytePositionInLine.Value}");
        }
        if (!jsonEx.LineNumber.HasValue && !jsonEx.BytePositionInLine.HasValue)
        {
            parts.Add($"HResult=0x{jsonEx.HResult:X8}");
        }
        return $"JSON 解析失败（{string.Join("; ", parts)}）";
    }
}
