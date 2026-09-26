using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Iris.Core.Languages;

internal sealed class LanguageCatalog : ILanguageCatalog
{
    private static readonly LanguageInfo[] Languages =
    [
        new(LanguageCodes.ChineseSimplified, "Simplified Chinese", "简体中文"),
        new(LanguageCodes.ChineseTraditional, "Traditional Chinese", "繁體中文"),
        new(LanguageCodes.English, "English", "English"),
        new(LanguageCodes.Japanese, "Japanese", "日本語"),
        new(LanguageCodes.Korean, "Korean", "한국어"),
        new(LanguageCodes.French, "French", "Français"),
        new(LanguageCodes.German, "German", "Deutsch"),
        new(LanguageCodes.Spanish, "Spanish", "Español"),
        new(LanguageCodes.Russian, "Russian", "Русский"),
        new(LanguageCodes.Portuguese, "Portuguese", "Português"),
        new(LanguageCodes.Italian, "Italian", "Italiano"),
        new(LanguageCodes.Vietnamese, "Vietnamese", "Tiếng Việt"),
        new(LanguageCodes.Thai, "Thai", "ไทย"),
        new(LanguageCodes.Arabic, "Arabic", "العربية"),
    ];

    private static readonly ReadOnlyCollection<LanguageInfo> AllLanguages = Array.AsReadOnly(Languages);

    private static readonly FrozenDictionary<string, LanguageInfo> CatalogByCode =
        Languages.ToFrozenDictionary(lang => lang.Code, lang => lang, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LanguageInfo> All => AllLanguages;

    public LanguageInfo? Find(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (string.Equals(trimmed, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return CatalogByCode.GetValueOrDefault(trimmed);
    }

    public bool IsSameFamily(string codeA, string codeB)
    {
        if (string.IsNullOrWhiteSpace(codeA) || string.IsNullOrWhiteSpace(codeB))
        {
            return false;
        }

        var trimmedA = codeA.Trim();
        var trimmedB = codeB.Trim();

        if (string.Equals(trimmedA, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmedB, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var subtagA = GetPrimarySubtag(trimmedA);
        var subtagB = GetPrimarySubtag(trimmedB);

        return subtagA.Equals(subtagB, StringComparison.OrdinalIgnoreCase);
    }

    public string GetPromptName(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "the auto-detected language";
        }

        var trimmed = code.Trim();
        if (string.Equals(trimmed, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return "the auto-detected language";
        }

        if (CatalogByCode.TryGetValue(trimmed, out var info))
        {
            return info.EnglishName;
        }

        return trimmed;
    }

    private static ReadOnlySpan<char> GetPrimarySubtag(ReadOnlySpan<char> code)
    {
        var hyphenIndex = code.IndexOf('-');
        return hyphenIndex >= 0 ? code[..hyphenIndex] : code;
    }
}
