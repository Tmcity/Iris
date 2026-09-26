using System;
using Iris.Core.Settings;

namespace Iris.Core.Languages;

internal sealed class TargetLanguageResolver : ITargetLanguageResolver
{
    private readonly ISettingsService _settings;
    private readonly ILanguageDetector _detector;
    private readonly ILanguageCatalog _catalog;

    public TargetLanguageResolver(
        ISettingsService settings,
        ILanguageDetector detector,
        ILanguageCatalog catalog)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public ResolvedLanguagePair Resolve(string text, string requestedSource, string? requestedTarget)
    {
        // 1. 规范化
        string? normalizedSource = null;
        if (!string.IsNullOrWhiteSpace(requestedSource))
        {
            var trimmedSource = requestedSource.Trim();
            if (!string.Equals(trimmedSource, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase))
            {
                var info = _catalog.Find(trimmedSource);
                normalizedSource = info != null ? info.Code : trimmedSource;
            }
        }

        string? normalizedTarget = null;
        var hasRequestedTarget = false;
        if (!string.IsNullOrWhiteSpace(requestedTarget))
        {
            var trimmedTarget = requestedTarget.Trim();
            if (!string.Equals(trimmedTarget, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase))
            {
                hasRequestedTarget = true;
                var info = _catalog.Find(trimmedTarget);
                normalizedTarget = info != null ? info.Code : trimmedTarget;
            }
        }

        // 2. Source 判定
        string finalSource;
        bool sourceWasDetected;

        if (normalizedSource != null)
        {
            finalSource = normalizedSource;
            sourceWasDetected = false;
        }
        else
        {
            var detection = _detector.Detect(text);
            finalSource = detection.Code;
            sourceWasDetected = true;
        }

        // 3. Target 判定
        string finalTarget;
        if (hasRequestedTarget && normalizedTarget != null)
        {
            finalTarget = normalizedTarget;
        }
        else
        {
            var settings = _settings.Current.Translation;
            var primary = NormalizeCode(settings.PrimaryTargetLanguage);
            var secondary = NormalizeCode(settings.SecondaryTargetLanguage);

            if (settings.AutoSwapTarget && _catalog.IsSameFamily(finalSource, primary))
            {
                finalTarget = secondary;
            }
            else
            {
                finalTarget = primary;
            }
        }

        // 4. 确保 Target 永不为 Auto
        if (string.IsNullOrWhiteSpace(finalTarget) ||
            string.Equals(finalTarget, LanguageCodes.Auto, StringComparison.OrdinalIgnoreCase))
        {
            finalTarget = LanguageCodes.ChineseSimplified;
        }

        return new ResolvedLanguagePair(finalSource, finalTarget, sourceWasDetected);
    }

    private string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return LanguageCodes.Auto;
        }

        var trimmed = code.Trim();
        var info = _catalog.Find(trimmed);
        return info != null ? info.Code : trimmed;
    }
}
