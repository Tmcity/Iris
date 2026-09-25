using System.Security.Cryptography;
using System.Text;

namespace Iris.Core.Diagnostics;

/// <summary>
/// 日志安全的文本处理工具。遵循 ARCH §12.5 隐私规范，禁止在日志中输出完整原文或译文。
/// </summary>
public static class LogText
{
    /// <summary>
    /// Information 及以上级别使用。格式：len=&lt;字符数&gt; sha=&lt;SHA-256 前 8 位小写十六进制&gt;；null 或空串返回 "len=0"。
    /// </summary>
    public static string Fingerprint(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "len=0";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var hex = Convert.ToHexStringLower(hash)[..8];
        return $"len={text.Length} sha={hex}";
    }

    /// <summary>
    /// 仅限 Debug 级别使用。格式：Fingerprint + ' head="&lt;前 32 个字符&gt;"'，超过 32 个字符时末尾加 "…"。
    /// </summary>
    public static string Preview(string? text)
    {
        var fp = Fingerprint(text);
        if (string.IsNullOrEmpty(text))
        {
            return $"{fp} head=\"\"";
        }

        string head;
        var hasMore = false;

        if (text.Length <= 32)
        {
            head = text;
        }
        else
        {
            hasMore = true;
            if (char.IsHighSurrogate(text[31]))
            {
                head = text[..31];
            }
            else
            {
                head = text[..32];
            }
        }

        var sb = new StringBuilder(head.Length + 8);
        foreach (var c in head)
        {
            switch (c)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '"':
                    sb.Append(@"\""");
                    break;
                case '\r':
                    sb.Append(@"\r");
                    break;
                case '\n':
                    sb.Append(@"\n");
                    break;
                case '\t':
                    sb.Append(@"\t");
                    break;
                default:
                    if (char.IsControl(c) || c == '\u0085' || c == '\u2028' || c == '\u2029')
                    {
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $@"\u{(int)c:X4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        var suffix = hasMore ? "…" : string.Empty;
        return $"{fp} head=\"{sb}{suffix}\"";
    }
}
