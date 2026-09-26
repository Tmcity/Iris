using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace Iris.Core.Languages;

internal sealed class ScriptLanguageDetector : ILanguageDetector
{
    private const double KanaThreshold = 0.05;
    private const double HangulThreshold = 0.30;
    private const double HanThreshold = 0.30;
    private const double CyrillicThreshold = 0.50;
    private const double ThaiThreshold = 0.50;
    private const double ArabicThreshold = 0.50;
    private const double LatinThreshold = 0.70;
    private const double EnglishStopWordThreshold = 0.15;
    private const int EnglishShortTextMaxWords = 2;
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

    private enum Script
    {
        Other,
        Kana,
        Hangul,
        Han,
        Cyrillic,
        Thai,
        Arabic,
        Latin,
    }

    private static Script Classify(Rune r)
    {
        var v = r.Value;
        return v switch
        {
            // 假名：3040–309F、30A0–30FF、31F0–31FF、FF66–FF9F
            >= 0x3040 and <= 0x309F => Script.Kana,
            >= 0x30A0 and <= 0x30FF => Script.Kana,
            >= 0x31F0 and <= 0x31FF => Script.Kana,
            >= 0xFF66 and <= 0xFF9F => Script.Kana,

            // 谚文：1100–11FF、3130–318F、A960–A97F、AC00–D7AF、D7B0–D7FF
            >= 0x1100 and <= 0x11FF => Script.Hangul,
            >= 0x3130 and <= 0x318F => Script.Hangul,
            >= 0xA960 and <= 0xA97F => Script.Hangul,
            >= 0xAC00 and <= 0xD7AF => Script.Hangul,
            >= 0xD7B0 and <= 0xD7FF => Script.Hangul,

            // 汉字：3400–4DBF、4E00–9FFF、F900–FAFF、20000–323AF
            >= 0x3400 and <= 0x4DBF => Script.Han,
            >= 0x4E00 and <= 0x9FFF => Script.Han,
            >= 0xF900 and <= 0xFAFF => Script.Han,
            >= 0x20000 and <= 0x323AF => Script.Han,

            // 西里尔：0400–052F
            >= 0x0400 and <= 0x052F => Script.Cyrillic,

            // 泰文：0E00–0E7F
            >= 0x0E00 and <= 0x0E7F => Script.Thai,

            // 阿拉伯：0600–06FF、0750–077F、08A0–08FF、FB50–FDFF、FE70–FEFF
            >= 0x0600 and <= 0x06FF => Script.Arabic,
            >= 0x0750 and <= 0x077F => Script.Arabic,
            >= 0x08A0 and <= 0x08FF => Script.Arabic,
            >= 0xFB50 and <= 0xFDFF => Script.Arabic,
            >= 0xFE70 and <= 0xFEFF => Script.Arabic,

            // 拉丁：0041–005A、0061–007A、00C0–024F、1E00–1EFF、FF21–FF3A、FF41–FF5A
            >= 0x0041 and <= 0x005A => Script.Latin,
            >= 0x0061 and <= 0x007A => Script.Latin,
            >= 0x00C0 and <= 0x024F => Script.Latin,
            >= 0x1E00 and <= 0x1EFF => Script.Latin,
            >= 0xFF21 and <= 0xFF3A => Script.Latin,
            >= 0xFF41 and <= 0xFF5A => Script.Latin,

            _ => Script.Other,
        };
    }

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
            var script = Classify(rune);
            switch (script)
            {
                case Script.Kana:
                    kanaCount++;
                    break;
                case Script.Hangul:
                    hangulCount++;
                    break;
                case Script.Han:
                    hanCount++;
                    break;
                case Script.Cyrillic:
                    cyrillicCount++;
                    break;
                case Script.Thai:
                    thaiCount++;
                    break;
                case Script.Arabic:
                    arabicCount++;
                    break;
                case Script.Latin:
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
        if (kanaCount * 100 >= letterCount * 5)
        {
            var confidence = Math.Min(1.0, (double)(kanaCount + hanCount) / letterCount);
            return new LanguageDetectionResult(LanguageCodes.Japanese, confidence);
        }

        // 谚文 ≥ 0.30 → ko
        if (hangulCount * 100 >= letterCount * 30)
        {
            return new LanguageDetectionResult(LanguageCodes.Korean, (double)hangulCount / letterCount);
        }

        // 汉字 ≥ 0.30 → zh-Hans
        if (hanCount * 100 >= letterCount * 30)
        {
            return new LanguageDetectionResult(LanguageCodes.ChineseSimplified, (double)hanCount / letterCount);
        }

        // 西里尔 ≥ 0.50 → ru
        if (cyrillicCount * 100 >= letterCount * 50)
        {
            return new LanguageDetectionResult(LanguageCodes.Russian, (double)cyrillicCount / letterCount);
        }

        // 泰文 ≥ 0.50 → th
        if (thaiCount * 100 >= letterCount * 50)
        {
            return new LanguageDetectionResult(LanguageCodes.Thai, (double)thaiCount / letterCount);
        }

        // 阿拉伯 ≥ 0.50 → ar
        if (arabicCount * 100 >= letterCount * 50)
        {
            return new LanguageDetectionResult(LanguageCodes.Arabic, (double)arabicCount / letterCount);
        }

        // 拉丁 ≥ 0.70 → 进入第 4 步英语判定
        if (latinCount * 100 >= letterCount * 70)
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

        // W ≤ 2 且每个词都只由 ASCII 字母、词内 '\''、词内 '-' 组成 → en，置信度 0.6
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
        if (wordCount >= 3)
        {
            var stopWordCount = 0;
            foreach (var word in words)
            {
                if (EnglishStopWords.Contains(word))
                {
                    stopWordCount++;
                }
            }

            if (stopWordCount * 100 >= wordCount * 15)
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
            if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '\'' || ch == '-'))
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

