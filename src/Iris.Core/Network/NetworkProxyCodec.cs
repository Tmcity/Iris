using System.Text;
using System.Text.Json;
using Iris.Core.Errors;
using Iris.Core.Settings;

namespace Iris.Core.Network;

/// <summary>Shared strict codec. Never includes input in an exception or diagnostic.</summary>
internal static class NetworkProxyCodec
{
    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");
    internal static IrisException Invalid() => new(ErrorCode.InvalidArgument, "代理配置无效，请检查地址和凭据。");
    internal static IrisException SecretUnavailable() => new(ErrorCode.SecretUnavailable, "代理凭据不可用，请重新输入。");
    internal static IrisException StorageFailure() => new(ErrorCode.StorageFailure, "网络配置无法安全保存或加载。");
    internal static IrisException Unavailable() => new(ErrorCode.NetworkUnavailable, "网络代理暂不可用。");

    internal static bool IsCredentialSetId(string? id) => id is { Length: 35 } && id.StartsWith("pc-", StringComparison.Ordinal)
        && id.AsSpan(3).IndexOfAnyExcept(HexDigits) < 0;

    internal static bool HasUserInfo(string text)
    {
        // Authority detection must still work for invalid host/port syntax. A path/query/fragment '@'
        // is not proxy authentication. Without a recognizable authority, refuse ambiguous legacy input.
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return text.Contains('@');
        var authority = text.AsSpan(schemeEnd + 3);
        int end = authority.IndexOfAny('/', '?', '#');
        return (end < 0 ? authority : authority[..end]).Contains('@');
    }

    internal static Uri ParseUri(string? text, bool allowUserInfo = false, bool system = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text != text.Trim()
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || !(uri.Scheme is "http" or "https" or "socks5" || system && uri.Scheme is "socks4" or "socks4a")
            || uri.AbsolutePath is not "" and not "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !allowUserInfo && HasUserInfo(text) || uri.Port is 0 or > 65535)
            throw Invalid();
        try
        {
            var builder = new UriBuilder(uri) { Host = uri.IdnHost, UserName = "", Password = "", Path = "/" };
            if (uri.Scheme.StartsWith("socks", StringComparison.Ordinal) && uri.Port < 0) builder.Port = 1080;
            return builder.Uri;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException) { throw Invalid(); }
    }

    internal static void ValidateCredentials(ProxyCredentials? credentials, Uri uri)
    {
        if (credentials == null) return;
        if (!ValidText(credentials.UserName, 1024) || !ValidText(credentials.Password, 4096)) throw Invalid();
        if (uri.Scheme == "socks5" && (Encoding.UTF8.GetByteCount(credentials.UserName) is < 1 or > 255
            || Encoding.UTF8.GetByteCount(credentials.Password) is < 1 or > 255)) throw Invalid();
    }

    private static bool ValidText(string? text, int budget)
    {
        if (text == null || text.Length > budget || text.IndexOfAny(['\0', '\r', '\n']) >= 0) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i])) return false;
        }
        return true;
    }

    internal static string Serialize(Uri uri, ProxyCredentials? credentials)
    {
        ValidateCredentials(credentials, uri);
        return JsonSerializer.Serialize(new
        {
            format = "iris-proxy-credentials", version = 1, proxyUri = uri.AbsoluteUri,
            userName = credentials?.UserName, password = credentials?.Password
        });
    }

    internal static ProxyCredentials? Deserialize(string? json, Uri expected)
    {
        try
        {
            if (json == null) throw SecretUnavailable();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw SecretUnavailable();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!names.Add(field.Name)) throw SecretUnavailable();
            if (names.Count != 5 || root.GetProperty("format").GetString() != "iris-proxy-credentials"
                || root.GetProperty("version").GetInt32() != 1
                || ParseUri(root.GetProperty("proxyUri").GetString()).AbsoluteUri != expected.AbsoluteUri) throw SecretUnavailable();
            var user = root.GetProperty("userName").GetString();
            var password = root.GetProperty("password").GetString();
            if (user == null && password == null) return null;
            if (user == null || password == null) throw SecretUnavailable();
            var credentials = new ProxyCredentials(user, password);
            ValidateCredentials(credentials, expected);
            return credentials;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IrisException)
        { throw SecretUnavailable(); }
    }

    internal static ProxyCredentials ReadUserInfo(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !HasUserInfo(text)) throw Invalid();
        var split = uri.UserInfo.IndexOf(':');
        for (int i = 0; i < uri.UserInfo.Length; i++)
        {
            if (uri.UserInfo[i] != '%') continue;
            if (i + 2 >= uri.UserInfo.Length || !Uri.IsHexDigit(uri.UserInfo[i + 1]) || !Uri.IsHexDigit(uri.UserInfo[i + 2])) throw Invalid();
            i += 2;
        }
        var result = new ProxyCredentials(Uri.UnescapeDataString(split < 0 ? uri.UserInfo : uri.UserInfo[..split]),
            split < 0 ? "" : Uri.UnescapeDataString(uri.UserInfo[(split + 1)..]));
        ValidateCredentials(result, ParseUri(text, allowUserInfo: true));
        return result;
    }
}
