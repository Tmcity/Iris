using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Providers.Http;

/// <summary>
/// 服务端错误信息与请求脱敏（ARCH v1.13 §12.5，T-010A H-3）。
/// </summary>
internal static partial class SecretRedactor
{
    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]{8,}")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)(access_token|api[_-]?key|secret[_-]?key)=[^&\s""']+")]
    private static partial Regex ParameterSecretRegex();

    /// <summary>
    /// 脱敏文本。按 ARCH v1.13 §12.5 与 T-010A H-3：
    /// 1. 先使用 SafeHttpDiagnostics.CleanUrlsInText 完整清理正文里的绝对 URL（移除 userinfo/query/fragment）；
    /// 2. 再按长度降序对所有非空已知凭据进行 Ordinal 精确替换（单遍非重叠替换，不再要求长度 >= 8，生成的 *** 不再被后续已知值替换）；
    /// 3. 最后应用常见密钥形态的正则表达式。
    /// </summary>
    public static string Redact(string text, IReadOnlyList<string?>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 1. URL 清理先于已知值替换，避免短凭据恰好出现在 scheme 中而使后续 URL 识别失败
        var result = SafeHttpDiagnostics.CleanUrlsInText(text);

        // 2. 对调用方明确提供的所有非空已知凭据进行最长优先、Ordinal 精确替换
        if (knownSecrets != null && knownSecrets.Count > 0)
        {
            var validSecrets = knownSecrets
                .Where(s => !string.IsNullOrEmpty(s) && s != "***")
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length)
                .ToList();

            if (validSecrets.Count > 0)
            {
                // Group by first character for fast candidate lookup; preserves length-descending order
                var byFirstChar = new Dictionary<char, List<string>>();
                foreach (var s in validSecrets)
                {
                    var first = s[0];
                    if (!byFirstChar.TryGetValue(first, out var list))
                    {
                        list = new List<string>();
                        byFirstChar[first] = list;
                    }
                    list.Add(s);
                }

                var textSpan = result.AsSpan();
                var sb = new StringBuilder(result.Length);
                var pos = 0;

                while (pos < textSpan.Length)
                {
                    var ch = textSpan[pos];
                    var matched = false;

                    if (byFirstChar.TryGetValue(ch, out var candidates))
                    {
                        var remaining = textSpan.Length - pos;
                        var slice = textSpan.Slice(pos);
                        foreach (var candidate in candidates)
                        {
                            if (candidate.Length <= remaining && slice.StartsWith(candidate.AsSpan(), StringComparison.Ordinal))
                            {
                                sb.Append("***");
                                pos += candidate.Length;
                                matched = true;
                                break;
                            }
                        }
                    }

                    if (!matched)
                    {
                        sb.Append(ch);
                        pos++;
                    }
                }

                result = sb.ToString();
            }
        }

        // 3. 未知凭据形态识别
        result = OpenAiKeyRegex().Replace(result, "sk-***");
        result = BearerTokenRegex().Replace(result, "Bearer ***");
        result = ParameterSecretRegex().Replace(result, "$1=***");

        return result;
    }
}
