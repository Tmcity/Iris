using System.Collections.Generic;
using System.Text;
using Iris.Core.Settings;
using Iris.Core.Text;

namespace Iris.Core.Translation;

internal sealed class TextPreprocessor : ITextPreprocessor
{
    private readonly ISettingsService _settings;

    public TextPreprocessor(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Prepare(string raw, TextOrigin origin)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // 1. 换行统一：\r\n、\r、U+0085、U+2028、U+2029 → \n
        // 2. 移除不可见字符：U+200B（零宽空格）、U+FEFF（BOM）、U+2060（单词连接符）。保留 U+200C 与 U+200D。
        // 3. U+00A0（不换行空格）→ 普通空格。
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            switch (ch)
            {
                case '\r':
                    if (i + 1 < raw.Length && raw[i + 1] == '\n')
                    {
                        i++;
                    }
                    sb.Append('\n');
                    break;
                case '\u0085':
                case '\u2028':
                case '\u2029':
                    sb.Append('\n');
                    break;
                case '\u200B':
                case '\uFEFF':
                case '\u2060':
                    // 移除不可见字符，跳过
                    break;
                case '\u00A0':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        var normalized = sb.ToString();

        // 4. 只含空白字符的行变为空行；连续多个空行压缩为 1 个空行。其他行的行内空白与缩进保持不变。
        var rawLines = normalized.Split('\n');
        var processedLines = new List<string>(rawLines.Length);
        var previousWasEmpty = false;

        foreach (var rawLine in rawLines)
        {
            var isEmpty = string.IsNullOrWhiteSpace(rawLine);
            if (isEmpty)
            {
                if (!previousWasEmpty)
                {
                    processedLines.Add(string.Empty);
                    previousWasEmpty = true;
                }
            }
            else
            {
                processedLines.Add(rawLine);
                previousWasEmpty = false;
            }
        }

        // 5. origin == Selection 且 SelectionLineBreaks == MergeSmart → 执行软换行合并。
        string result;
        var selectionLineBreaks = _settings.Current.Translation.SelectionLineBreaks;
        if (origin == TextOrigin.Selection && selectionLineBreaks == LineBreakHandling.MergeSmart)
        {
            result = SoftLineBreakMerger.Merge(processedLines);
        }
        else
        {
            result = string.Join("\n", processedLines);
        }

        // 6. 去除整段文本的首尾空白（string.Trim()）。
        return result.Trim();
    }
}
