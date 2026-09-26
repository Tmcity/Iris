using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace Iris.Core.Languages;

internal sealed class ScriptLanguageDetector : ILanguageDetector
{
    private const int KanaThresholdPercent = 5;
    private const int HangulThresholdPercent = 30;
    private const int HanThresholdPercent = 30;
    private const int CyrillicThresholdPercent = 50;
    private const int ThaiThresholdPercent = 50;
    private const int ArabicThresholdPercent = 50;
    private const int LatinThresholdPercent = 70;
    private const int EnglishStopWordThresholdPercent = 15;
    private const int EnglishShortTextMaxWords = 2;
    private const int EnglishLongTextMinWords = 3;
    private const double EnglishShortTextConfidence = 0.6;

    private static readonly FrozenSet<string> EnglishStopWords = new[]
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "am", "do", "does", "did", "have", "has", "had", "i", "you", "he",
        "she", "it", "we", "they", "me", "him", "her", "us", "them", "my",
        "your", "his", "its", "our", "their", "this", "that", "these", "those", "and",
        "or", "but", "not", "no", "of", "to", "in", "on", "at", "by",
        "for", "with", "from", "as", "if", "so", "than", "then", "there", "what",
        "which", "who", "how", "can", "will", "would", "should", "could",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public LanguageDetectionResult Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LanguageDetectionResult(LanguageCodes.Auto, 0.0);
        }

        var letterCount = 0;
        var kanaCount = 0;
        var hangulCount = 0;
        var hanCount = 0;
        var cyrillicCount = 0;
        var thaiCount = 0;
        var arabicCount = 0;
        var latinCount = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            letterCount++;
            var script = UnicodeScripts.Classify(rune);
            switch (script)
            {
                case UnicodeScript.Kana:
                    kanaCount++;
                    break;
                case UnicodeScript.Hangul:
                    hangulCount++;
                    break;
                case UnicodeScript.Han:
                    hanCount++;
                    break;
                case UnicodeScript.Cyrillic:
                    cyrillicCount++;
                    break;
                case UnicodeScript.Thai:
                    thaiCount++;
                    break;
                case UnicodeScript.Arabic:
                    arabicCount++;
                    break;
                case UnicodeScript.Latin:
                    latinCount++;
                    break;
            }
        }

        if (letterCount == 0)
        {
            return new LanguageDetectionResult(LanguageCodes.Auto, 0.0);
        }

        // 3. 按顺序判定，命中即返回（整数比较避免浮点误差）
        // 假名 ≥ 0.05 → ja（置信度取假名与汉字比例之和，上限 1）
        if (kanaCount * 100 >= letterCount * KanaThresholdPercent)
        {
            var confidence = Math.Min(1.0, (double)(kanaCount + hanCount) / letterCount);
            return new LanguageDetectionResult(LanguageCodes.Japanese, confidence);
        }

        // 谚文 ≥ 0.30 → ko
        if (hangulCount * 100 >= letterCount * HangulThresholdPercent)
        {
            return new LanguageDetectionResult(LanguageCodes.Korean, (double)hangulCount / letterCount);
        }

        // 汉字 ≥ 0.30 → zh-Hans
        if (hanCount * 100 >= letterCount * HanThresholdPercent)
        {
            return new LanguageDetectionResult(LanguageCodes.ChineseSimplified, (double)hanCount / letterCount);
        }

        // 西里尔 ≥ 0.50 → ru
        if (cyrillicCount * 100 >= letterCount * CyrillicThresholdPercent)
        {
            return new LanguageDetectionResult(LanguageCodes.Russian, (double)cyrillicCount / letterCount);
        }

        // 泰文 ≥ 0.50 → th
        if (thaiCount * 100 >= letterCount * ThaiThresholdPercent)
        {
            return new LanguageDetectionResult(LanguageCodes.Thai, (double)thaiCount / letterCount);
        }

        // 阿拉伯 ≥ 0.50 → ar
        if (arabicCount * 100 >= letterCount * ArabicThresholdPercent)
        {
            return new LanguageDetectionResult(LanguageCodes.Arabic, (double)arabicCount / letterCount);
        }

        // 拉丁 ≥ 0.70 → 进入第 4 步英语判定
        if (latinCount * 100 >= letterCount * LatinThresholdPercent)
        {
            return DetectEnglish(text, latinCount, letterCount);
        }

        return new LanguageDetectionResult(LanguageCodes.Auto, 0.0);
    }

    private static LanguageDetectionResult DetectEnglish(string text, int latinCount, int letterCount)
    {
        var rawTokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>(rawTokens.Length);

        foreach (var token in rawTokens)
        {
            var trimmedSpan = TrimNonLetters(token.AsSpan());
            if (!trimmedSpan.IsEmpty)
            {
                words.Add(trimmedSpan.ToString());
            }
        }

        var wordCount = words.Count;
        if (wordCount == 0)
        {
            return new LanguageDetectionResult(LanguageCodes.Auto, 0.0);
        }

        // W ≤ 2 且每个词都只由 ASCII 字母、词内 '\'' 或 '’'、词内 '-' 组成 → en，置信度 0.6
        if (wordCount <= EnglishShortTextMaxWords)
        {
            var allAsciiShort = true;
            foreach (var word in words)
            {
                if (!IsAsciiWordWithInternalPunctuation(word))
                {
                    allAsciiShort = false;
                    break;
                }
            }

            if (allAsciiShort)
            {
                return new LanguageDetectionResult(LanguageCodes.English, EnglishShortTextConfidence);
            }
        }

        // W ≥ 3 且英文停用词占比 ≥ 0.15 → en，置信度 = 拉丁 ratio
        if (wordCount >= EnglishLongTextMinWords)
        {
            var stopWordCount = 0;
            foreach (var word in words)
            {
                if (EnglishStopWords.Contains(word))
                {
                    stopWordCount++;
                }
            }

            if (stopWordCount * 100 >= wordCount * EnglishStopWordThresholdPercent)
            {
                var latinRatio = (double)latinCount / letterCount;
                return new LanguageDetectionResult(LanguageCodes.English, latinRatio);
            }
        }

        return new LanguageDetectionResult(LanguageCodes.Auto, 0.0);
    }

    private static bool IsAsciiWordWithInternalPunctuation(string word)
    {
        foreach (var ch in word)
        {
            if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '\'' || ch == '\u2019' || ch == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimNonLetters(ReadOnlySpan<char> span)
    {
        var start = 0;
        while (start < span.Length)
        {
            var status = Rune.DecodeFromUtf16(span[start..], out var rune, out var charsConsumed);
            if (status != System.Buffers.OperationStatus.Done || charsConsumed == 0)
            {
                start++;
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                break;
            }

            start += charsConsumed;
        }

        var end = span.Length;
        while (end > start)
        {
            var status = Rune.DecodeLastFromUtf16(span[..end], out var rune, out var charsConsumed);
            if (status != System.Buffers.OperationStatus.Done || charsConsumed == 0)
            {
                end--;
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                break;
            }

            end -= charsConsumed;
        }

        return start < end ? span[start..end] : [];
    }
}
