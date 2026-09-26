using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Iris.Core.Languages;

namespace Iris.Core.Text;

internal static partial class SoftLineBreakMerger
{
    private static readonly SearchValues<char> ClosingSymbols = SearchValues.Create(
        ['"', '\'', '\u201D', '\u2019', '\uFF09', ')', '\u300D', '\u300F', '\u3011', ']', '\u300B']);

    private static readonly SearchValues<char> SentencePunctuation = SearchValues.Create(
        ['\u3002', '\uFF01', '\uFF1F', '.', '!', '?', '\uFF1A', ':', '\uFF1B', ';']);

    [GeneratedRegex(@"^\d{1,3}[.)、]")]
    private static partial Regex NumberedListRegex();

    [GeneratedRegex(@"^[（(]\d{1,3}[)）]")]
    private static partial Regex ParenthesizedListRegex();

    /// <summary>
    /// 按 ARCH §7.5“软换行合并”规则合并 lines。空行（长度为 0）作为段落分隔保留。
    /// isParagraphBreakAfter(i) 返回 true 表示第 i 行之后必须保留换行（OCR 用；文本预处理传 null）。
    /// 返回用 '\n' 连接的结果。
    /// </summary>
    public static string Merge(IReadOnlyList<string> lines, Func<int, bool>? isParagraphBreakAfter = null)
    {
        if (lines == null || lines.Count == 0)
        {
            return string.Empty;
        }

        var resultLines = new List<string>();
        string? currentLine = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (line.Length == 0)
            {
                if (currentLine != null)
                {
                    resultLines.Add(currentLine);
                    currentLine = null;
                }
                resultLines.Add(string.Empty);
                continue;
            }

            if (currentLine == null)
            {
                currentLine = line;
                continue;
            }

            // 检查 currentLine 与 line 之间是否保留换行（index 为 i - 1）
            if (ShouldKeepLineBreak(currentLine, line, i - 1, isParagraphBreakAfter))
            {
                resultLines.Add(currentLine);
                currentLine = line;
            }
            else
            {
                currentLine = MergeTwoLines(currentLine, line);
            }
        }

        if (currentLine != null)
        {
            resultLines.Add(currentLine);
        }

        return string.Join("\n", resultLines);
    }

    private static bool ShouldKeepLineBreak(string a, string b, int prevIndex, Func<int, bool>? isParagraphBreakAfter)
    {
        // 1. A 去掉末尾空白以及末尾的收尾符号（" ' ” ’ ） ) 」 』 】 ] 》）之后，以句末标点结尾：。 ！ ？ . ! ? ： : ； ;
        var aSpan = a.AsSpan().TrimEnd();
        while (!aSpan.IsEmpty && (ClosingSymbols.Contains(aSpan[^1]) || char.IsWhiteSpace(aSpan[^1])))
        {
            aSpan = aSpan[..^1].TrimEnd();
        }
        if (!aSpan.IsEmpty && SentencePunctuation.Contains(aSpan[^1]))
        {
            return true;
        }

        // 2. B 去掉开头空白后，以列表标记开头：- 、* 、• 、· （符号后必须有空格），或匹配 ^\d{1,3}[.)、]、^[（(]\d{1,3}[)）]
        var bSpan = b.AsSpan().TrimStart();
        if (StartsWithListMarker(bSpan))
        {
            return true;
        }

        // 3. isParagraphBreakAfter(prevIndex) 返回 true
        if (isParagraphBreakAfter != null && isParagraphBreakAfter(prevIndex))
        {
            return true;
        }

        return false;
    }

    private static bool StartsWithListMarker(ReadOnlySpan<char> span)
    {
        if (span.Length >= 2)
        {
            var first = span[0];
            var second = span[1];
            if (second == ' ' && (first == '-' || first == '*' || first == '\u2022' || first == '\u00B7'))
            {
                return true;
            }
        }

        if (NumberedListRegex().IsMatch(span))
        {
            return true;
        }

        if (ParenthesizedListRegex().IsMatch(span))
        {
            return true;
        }

        return false;
    }

    private static string MergeTwoLines(string a, string b)
    {
        var aTrimmed = a.AsSpan().TrimEnd();
        var bTrimmed = b.AsSpan().TrimStart();

        if (aTrimmed.IsEmpty)
        {
            return bTrimmed.ToString();
        }
        if (bTrimmed.IsEmpty)
        {
            return aTrimmed.ToString();
        }

        // 1. A 以 - 结尾、- 前一个字符是拉丁字母、且 B 以拉丁字母开头 → 去掉 - 后直接拼接
        if (aTrimmed.Length >= 2 && aTrimmed[^1] == '-')
        {
            var statusA = Rune.DecodeLastFromUtf16(aTrimmed[..^1], out var runeBeforeHyphen, out _);
            var statusB = Rune.DecodeFromUtf16(bTrimmed, out var firstRuneB, out _);

            if (statusA == OperationStatus.Done && statusB == OperationStatus.Done &&
                UnicodeScripts.Classify(runeBeforeHyphen) == UnicodeScript.Latin &&
                UnicodeScripts.Classify(firstRuneB) == UnicodeScript.Latin)
            {
                return string.Concat(aTrimmed[..^1], bTrimmed);
            }
        }

        // 2. A 的最后一个字符与 B 的第一个字符都属于 CJK → 直接拼接，不加空格
        {
            var statusA = Rune.DecodeLastFromUtf16(aTrimmed, out var lastRuneA, out _);
            var statusB = Rune.DecodeFromUtf16(bTrimmed, out var firstRuneB, out _);

            if (statusA == OperationStatus.Done && statusB == OperationStatus.Done &&
                UnicodeScripts.IsCjkForLineMerge(lastRuneA) &&
                UnicodeScripts.IsCjkForLineMerge(firstRuneB))
            {
                return string.Concat(aTrimmed, bTrimmed);
            }
        }

        // 3. 其他情况 → 用一个空格拼接
        return string.Concat(aTrimmed, " ", bTrimmed);
    }
}
