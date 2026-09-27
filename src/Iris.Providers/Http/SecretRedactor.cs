using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Iris.Providers.Http;

/// <summary>
/// 服务端错误信息与请求脱敏。
/// </summary>
internal static partial class SecretRedactor
{
    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]{8,}")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)(access_token|api[_-]?key|secret[_-]?key)=[^&\s""']+")]
    private static partial Regex ParameterSecretRegex();

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://)[^\s/@:'""]+:[^\s/@'""]+@")]
    private static partial Regex UrlCredentialsRegex();

    /// <summary>
    /// 脱敏文本。按 ARCH §12.5：
    /// 1. 先替换已知密钥（按长度降序排列，仅替换长度 >= 8 的密钥）；
    /// 2. 再应用常见密钥形态的四个正则表达式。
    /// </summary>
    public static string Redact(string text, IReadOnlyList<string?>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        if (knownSecrets != null && knownSecrets.Count > 0)
        {
            var validSecrets = knownSecrets
                .Where(s => !string.IsNullOrEmpty(s) && s.Length >= 8)
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length);

            foreach (var secret in validSecrets)
            {
                result = result.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        result = OpenAiKeyRegex().Replace(result, "sk-***");
        result = BearerTokenRegex().Replace(result, "Bearer ***");
        result = ParameterSecretRegex().Replace(result, "$1=***");
        result = UrlCredentialsRegex().Replace(result, "$1***@");

        return result;
    }
}
