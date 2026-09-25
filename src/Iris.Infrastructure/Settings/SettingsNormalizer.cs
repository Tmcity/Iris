using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Actions;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Core.Translation;

namespace Iris.Infrastructure.Settings;

/// <summary>
/// 设置规范化器：纯函数，保证 AppSettings 永远满足 N1–N11 不变量。
/// 若未发生任何修复，返回原实例。
/// </summary>
internal static class SettingsNormalizer
{
    private static readonly string[] AllowedLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];

    public static AppSettings Normalize(AppSettings input, ICollection<string> fixes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(fixes);

        var initialFixCount = fixes.Count;

        // N1 顶层节与 SchemaVersion
        var schemaVersion = input.SchemaVersion;
        if (schemaVersion != AppSettings.CurrentSchemaVersion)
        {
            fixes.Add($"SchemaVersion: {schemaVersion} → {AppSettings.CurrentSchemaVersion}");
            schemaVersion = AppSettings.CurrentSchemaVersion;
        }

        var general = input.General ?? new GeneralSettings();
        if (input.General == null) fixes.Add("General: null → 默认值");

        var translation = input.Translation ?? new TranslationSettings();
        if (input.Translation == null) fixes.Add("Translation: null → 默认值");

        var ocr = input.Ocr ?? new OcrSettings();
        if (input.Ocr == null) fixes.Add("Ocr: null → 默认值");

        var selection = input.Selection ?? new SelectionSettings();
        if (input.Selection == null) fixes.Add("Selection: null → 默认值");

        var screenshot = input.Screenshot ?? new ScreenshotSettings();
        if (input.Screenshot == null) fixes.Add("Screenshot: null → 默认值");

        var floatingBall = input.FloatingBall ?? new FloatingBallSettings();
        if (input.FloatingBall == null) fixes.Add("FloatingBall: null → 默认值");

        var translateWindow = input.TranslateWindow ?? new TranslateWindowSettings();
        if (input.TranslateWindow == null) fixes.Add("TranslateWindow: null → 默认值");

        var history = input.History ?? new HistorySettings();
        if (input.History == null) fixes.Add("History: null → 默认值");

        var network = input.Network ?? new NetworkSettings();
        if (input.Network == null) fixes.Add("Network: null → 默认值");

        if (input.Hotkeys == null) fixes.Add("Hotkeys: null → 默认值");

        // 各节独立规范化
        var normalizedGeneral = NormalizeGeneral(general, fixes);
        var normalizedHotkeys = NormalizeHotkeys(input.Hotkeys, fixes);
        var normalizedTranslation = NormalizeTranslation(translation, fixes);
        var normalizedOcr = NormalizeOcr(ocr, fixes);
        var normalizedSelection = NormalizeSelection(selection, fixes);
        var normalizedScreenshot = NormalizeScreenshot(screenshot, fixes);
        var normalizedFloatingBall = NormalizeFloatingBall(floatingBall, fixes);
        var normalizedTranslateWindow = NormalizeTranslateWindow(translateWindow, fixes);
        var normalizedHistory = NormalizeHistory(history, fixes);
        var normalizedNetwork = NormalizeNetwork(network, fixes);

        if (fixes.Count == initialFixCount)
        {
            return input;
        }

        return new AppSettings
        {
            SchemaVersion = schemaVersion,
            General = normalizedGeneral,
            Hotkeys = normalizedHotkeys,
            Translation = normalizedTranslation,
            Ocr = normalizedOcr,
            Selection = normalizedSelection,
            Screenshot = normalizedScreenshot,
            FloatingBall = normalizedFloatingBall,
            TranslateWindow = normalizedTranslateWindow,
            History = normalizedHistory,
            Network = normalizedNetwork,
        };
    }

    private static GeneralSettings NormalizeGeneral(GeneralSettings general, ICollection<string> fixes)
    {
        // N11
        var uiLang = general.UiLanguage;
        string canonicalLang;
        if (string.Equals(uiLang, "zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            canonicalLang = "zh-CN";
        }
        else if (string.Equals(uiLang, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            canonicalLang = "en-US";
        }
        else
        {
            canonicalLang = "zh-CN";
        }

        if (!string.Equals(uiLang, canonicalLang, StringComparison.Ordinal))
        {
            fixes.Add($"General.UiLanguage: {uiLang} → {canonicalLang}");
        }

        var logLevel = general.LogLevel;
        var canonicalLevel = AllowedLogLevels.FirstOrDefault(l => string.Equals(l, logLevel, StringComparison.OrdinalIgnoreCase));
        if (canonicalLevel == null)
        {
            canonicalLevel = "Information";
            fixes.Add($"General.LogLevel: {logLevel} → Information");
        }
        else if (!string.Equals(logLevel, canonicalLevel, StringComparison.Ordinal))
        {
            fixes.Add($"General.LogLevel: {logLevel} → {canonicalLevel}");
        }

        if (string.Equals(general.UiLanguage, canonicalLang, StringComparison.Ordinal) &&
            string.Equals(general.LogLevel, canonicalLevel, StringComparison.Ordinal))
        {
            return general;
        }

        return general with
        {
            UiLanguage = canonicalLang,
            LogLevel = canonicalLevel,
        };
    }

    private static Dictionary<IrisAction, HotkeyBinding> NormalizeHotkeys(
        IReadOnlyDictionary<IrisAction, HotkeyBinding>? hotkeys,
        ICollection<string> fixes)
    {
        // N2
        var defaults = HotkeyDefaults.Create();
        var result = new Dictionary<IrisAction, HotkeyBinding>();

        if (hotkeys != null)
        {
            foreach (var (action, binding) in hotkeys)
            {
                if (binding == null)
                {
                    // 值为 null 的条目视为缺失，后续补默认
                    continue;
                }

                if (string.IsNullOrWhiteSpace(binding.Key))
                {
                    if (defaults.TryGetValue(action, out var defaultBinding))
                    {
                        result[action] = defaultBinding;
                        fixes.Add($"Hotkeys[{action}]: 空白键名 → 默认绑定");
                    }
                    else
                    {
                        fixes.Add($"Hotkeys[{action}]: 空白键名 → 已移除");
                    }
                }
                else
                {
                    result[action] = binding;
                }
            }
        }

        foreach (var (defaultAction, defaultBinding) in defaults)
        {
            if (!result.ContainsKey(defaultAction))
            {
                result[defaultAction] = defaultBinding;
                fixes.Add($"Hotkeys[{defaultAction}]: 缺失 → 默认绑定");
            }
        }

        return result;
    }

    private static TranslationSettings NormalizeTranslation(TranslationSettings translation, ICollection<string> fixes)
    {
        // N3
        var maxConcurrent = Clamp(translation.MaxConcurrentProviders, 1, 8, "Translation.MaxConcurrentProviders", fixes);
        var maxInputChars = Clamp(translation.MaxInputChars, 100, 100000, "Translation.MaxInputChars", fixes);
        var cacheCapacity = Clamp(translation.CacheCapacity, 0, 10000, "Translation.CacheCapacity", fixes);

        var primaryTarget = translation.PrimaryTargetLanguage;
        if (string.IsNullOrWhiteSpace(primaryTarget) || string.Equals(primaryTarget, "auto", StringComparison.OrdinalIgnoreCase))
        {
            fixes.Add($"Translation.PrimaryTargetLanguage: {primaryTarget} → {LanguageCodes.ChineseSimplified}");
            primaryTarget = LanguageCodes.ChineseSimplified;
        }

        var secondaryTarget = translation.SecondaryTargetLanguage;
        if (string.IsNullOrWhiteSpace(secondaryTarget) || string.Equals(secondaryTarget, "auto", StringComparison.OrdinalIgnoreCase))
        {
            fixes.Add($"Translation.SecondaryTargetLanguage: {secondaryTarget} → {LanguageCodes.English}");
            secondaryTarget = LanguageCodes.English;
        }

        var defaultSource = translation.DefaultSourceLanguage;
        if (string.IsNullOrWhiteSpace(defaultSource))
        {
            fixes.Add($"Translation.DefaultSourceLanguage: {defaultSource} → {LanguageCodes.Auto}");
            defaultSource = LanguageCodes.Auto;
        }

        // CustomPrompts 规范化
        var customPrompts = translation.CustomPrompts;
        if (customPrompts == null)
        {
            fixes.Add("Translation.CustomPrompts: null → []");
            customPrompts = [];
        }

        var normalizedPrompts = new List<PromptTemplate>();
        var seenPromptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prompt in customPrompts)
        {
            if (string.IsNullOrWhiteSpace(prompt.Id) ||
                string.Equals(prompt.Id, BuiltInPrompts.DefaultTemplateId, StringComparison.OrdinalIgnoreCase) ||
                !seenPromptIds.Add(prompt.Id))
            {
                fixes.Add($"Translation.CustomPrompts[{prompt.Id}]: 非法或与内置模板冲突 → 已移除");
                continue;
            }

            normalizedPrompts.Add(prompt);
        }

        // DefaultPromptTemplateId 必须存在于内置模板或自定义模板中
        var defaultPromptId = translation.DefaultPromptTemplateId;
        var promptExists = string.Equals(defaultPromptId, BuiltInPrompts.DefaultTemplateId, StringComparison.OrdinalIgnoreCase) ||
                           normalizedPrompts.Any(p => string.Equals(p.Id, defaultPromptId, StringComparison.OrdinalIgnoreCase));

        if (!promptExists)
        {
            fixes.Add($"Translation.DefaultPromptTemplateId: {defaultPromptId} → {BuiltInPrompts.DefaultTemplateId}");
            defaultPromptId = BuiltInPrompts.DefaultTemplateId;
        }

        // Providers 规范化
        var rawProviders = translation.Providers;
        if (rawProviders == null)
        {
            fixes.Add("Translation.Providers: null → []");
            rawProviders = [];
        }

        var normalizedProviders = new List<TranslationProviderProfile>();
        var seenProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in rawProviders)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                fixes.Add("Translation.Providers: 缺少 Id → 已移除");
                continue;
            }

            if (!seenProviderIds.Add(profile.Id))
            {
                fixes.Add($"Translation.Providers[{profile.Id}]: 重复 Id → 已移除");
                continue;
            }

            var displayName = profile.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                fixes.Add($"Translation.Providers[{profile.Id}].DisplayName: 空白 → {profile.Id}");
                displayName = profile.Id;
            }

            var timeout = Clamp(profile.TimeoutSeconds, 5, 600, $"Translation.Providers[{profile.Id}].TimeoutSeconds", fixes);

            int? maxOutputTokens = profile.MaxOutputTokens;
            if (maxOutputTokens.HasValue && maxOutputTokens.Value < 1)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].MaxOutputTokens: {maxOutputTokens.Value} → null");
                maxOutputTokens = null;
            }

            double? temp = profile.Temperature;
            if (temp.HasValue)
            {
                var clampedTemp = Clamp(temp.Value, 0.0, 2.0, $"Translation.Providers[{profile.Id}].Temperature", fixes);
                temp = clampedTemp;
            }

            var enabled = profile.Enabled;
            if (profile.Kind == TranslationProviderKind.OpenAICompatible && string.IsNullOrWhiteSpace(profile.BaseUrl) && enabled)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].Enabled: true → false");
                enabled = false;
            }

            var openAIOptions = profile.OpenAIOptions;
            if (profile.Kind == TranslationProviderKind.OpenAICompatible && openAIOptions == null)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].OpenAIOptions: null → 默认值");
                openAIOptions = new OpenAICompatibleOptions();
            }

            var anthropicOptions = profile.AnthropicOptions;
            if (profile.Kind == TranslationProviderKind.Anthropic && anthropicOptions == null)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].AnthropicOptions: null → 默认值");
                anthropicOptions = new AnthropicOptions();
            }

            normalizedProviders.Add(profile with
            {
                DisplayName = displayName,
                TimeoutSeconds = timeout,
                MaxOutputTokens = maxOutputTokens,
                Temperature = temp,
                Enabled = enabled,
                OpenAIOptions = openAIOptions,
                AnthropicOptions = anthropicOptions,
            });
        }

        return translation with
        {
            MaxConcurrentProviders = maxConcurrent,
            MaxInputChars = maxInputChars,
            CacheCapacity = cacheCapacity,
            PrimaryTargetLanguage = primaryTarget,
            SecondaryTargetLanguage = secondaryTarget,
            DefaultSourceLanguage = defaultSource,
            CustomPrompts = normalizedPrompts,
            DefaultPromptTemplateId = defaultPromptId,
            Providers = normalizedProviders,
        };
    }

    private static OcrSettings NormalizeOcr(OcrSettings ocr, ICollection<string> fixes)
    {
        // N4
        var rawProviders = ocr.Providers;
        if (rawProviders == null)
        {
            fixes.Add("Ocr.Providers: null → 默认列表");
            rawProviders = new OcrSettings().Providers;
        }

        var normalizedProviders = new List<OcrProviderProfile>();
        var seenProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in rawProviders)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                fixes.Add("Ocr.Providers: 缺少 Id → 已移除");
                continue;
            }

            if (!seenProviderIds.Add(profile.Id))
            {
                fixes.Add($"Ocr.Providers[{profile.Id}]: 重复 Id → 已移除");
                continue;
            }

            var displayName = profile.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                fixes.Add($"Ocr.Providers[{profile.Id}].DisplayName: 空白 → {profile.Id}");
                displayName = profile.Id;
            }

            var timeout = Clamp(profile.TimeoutSeconds, 5, 120, $"Ocr.Providers[{profile.Id}].TimeoutSeconds", fixes);

            var baiduOptions = profile.BaiduOptions;
            if (profile.Kind == OcrProviderKind.BaiduCloud && baiduOptions == null)
            {
                fixes.Add($"Ocr.Providers[{profile.Id}].BaiduOptions: null → 默认值");
                baiduOptions = new BaiduOcrOptions();
            }

            var windowsOptions = profile.WindowsOptions;
            if (profile.Kind == OcrProviderKind.WindowsLocal && windowsOptions == null)
            {
                fixes.Add($"Ocr.Providers[{profile.Id}].WindowsOptions: null → 默认值");
                windowsOptions = new WindowsOcrOptions();
            }

            normalizedProviders.Add(profile with
            {
                DisplayName = displayName,
                TimeoutSeconds = timeout,
                BaiduOptions = baiduOptions,
                WindowsOptions = windowsOptions,
            });
        }

        // 补齐缺失的内置 Profile
        if (!normalizedProviders.Any(p => string.Equals(p.Id, BuiltInProviderIds.BaiduOcr, StringComparison.OrdinalIgnoreCase)))
        {
            fixes.Add("Ocr.Providers: 缺少内置 ocr-baidu → 已追加默认配置");
            normalizedProviders.Add(new OcrProviderProfile
            {
                Id = BuiltInProviderIds.BaiduOcr,
                Kind = OcrProviderKind.BaiduCloud,
                DisplayName = "百度 OCR",
                BaiduOptions = new(),
            });
        }

        if (!normalizedProviders.Any(p => string.Equals(p.Id, BuiltInProviderIds.WindowsOcr, StringComparison.OrdinalIgnoreCase)))
        {
            fixes.Add("Ocr.Providers: 缺少内置 ocr-windows → 已追加默认配置");
            normalizedProviders.Add(new OcrProviderProfile
            {
                Id = BuiltInProviderIds.WindowsOcr,
                Kind = OcrProviderKind.WindowsLocal,
                DisplayName = "Windows 离线 OCR",
                WindowsOptions = new(),
            });
        }

        var activeId = ocr.ActiveProviderId;
        if (!normalizedProviders.Any(p => string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase)))
        {
            fixes.Add($"Ocr.ActiveProviderId: {activeId} → {BuiltInProviderIds.BaiduOcr}");
            activeId = BuiltInProviderIds.BaiduOcr;
        }

        var fallbackId = ocr.FallbackProviderId;
        if (fallbackId != null)
        {
            if (string.Equals(fallbackId, activeId, StringComparison.OrdinalIgnoreCase) ||
                !normalizedProviders.Any(p => string.Equals(p.Id, fallbackId, StringComparison.OrdinalIgnoreCase)))
            {
                fixes.Add($"Ocr.FallbackProviderId: {fallbackId} → null");
                fallbackId = null;
            }
        }

        var langHint = ocr.LanguageHint;
        if (string.IsNullOrWhiteSpace(langHint))
        {
            fixes.Add($"Ocr.LanguageHint: {langHint} → {LanguageCodes.Auto}");
            langHint = LanguageCodes.Auto;
        }

        return ocr with
        {
            Providers = normalizedProviders,
            ActiveProviderId = activeId,
            FallbackProviderId = fallbackId,
            LanguageHint = langHint,
        };
    }

    private static SelectionSettings NormalizeSelection(SelectionSettings selection, ICollection<string> fixes)
    {
        // N5
        var readOrder = selection.ReadOrder;
        if (readOrder == null || readOrder.Count == 0)
        {
            fixes.Add("Selection.ReadOrder: 空白 → 默认顺序");
            readOrder = [SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard];
        }
        else
        {
            var distinct = readOrder.Distinct().ToList();
            if (distinct.Count != readOrder.Count)
            {
                fixes.Add("Selection.ReadOrder: 存在重复项 → 已去重");
                readOrder = distinct;
            }
        }

        var clipboardTimeout = Clamp(selection.ClipboardTimeoutMs, 100, 2000, "Selection.ClipboardTimeoutMs", fixes);
        var minDrag = Clamp(selection.MinDragDistancePx, 2, 50, "Selection.MinDragDistancePx", fixes);
        var autoHide = Clamp(selection.IconAutoHideMs, 500, 10000, "Selection.IconAutoHideMs", fixes);

        var excluded = selection.ExcludedProcesses;
        if (excluded == null)
        {
            fixes.Add("Selection.ExcludedProcesses: null → []");
            excluded = [];
        }

        var unsafeProcesses = selection.ClipboardUnsafeProcesses;
        if (unsafeProcesses == null)
        {
            fixes.Add("Selection.ClipboardUnsafeProcesses: null → 默认列表");
            unsafeProcesses = new SelectionSettings().ClipboardUnsafeProcesses;
        }

        return selection with
        {
            ReadOrder = readOrder,
            ClipboardTimeoutMs = clipboardTimeout,
            MinDragDistancePx = minDrag,
            IconAutoHideMs = autoHide,
            ExcludedProcesses = excluded,
            ClipboardUnsafeProcesses = unsafeProcesses,
        };
    }

    private static ScreenshotSettings NormalizeScreenshot(ScreenshotSettings screenshot, ICollection<string> fixes)
    {
        // N6
        var opacity = Clamp(screenshot.MaskOpacity, 0.0, 0.9, "Screenshot.MaskOpacity", fixes);
        return screenshot with { MaskOpacity = opacity };
    }

    private static FloatingBallSettings NormalizeFloatingBall(FloatingBallSettings fb, ICollection<string> fixes)
    {
        // N7
        var size = Clamp(fb.Size, 24.0, 96.0, "FloatingBall.Size", fixes);
        var opacity = Clamp(fb.Opacity, 0.2, 1.0, "FloatingBall.Opacity", fixes);
        return fb with { Size = size, Opacity = opacity };
    }

    private static TranslateWindowSettings NormalizeTranslateWindow(TranslateWindowSettings tw, ICollection<string> fixes)
    {
        // N8
        var width = Clamp(tw.Width, 280.0, 1200.0, "TranslateWindow.Width", fixes);
        var fontSize = Clamp(tw.FontSize, 10.0, 32.0, "TranslateWindow.FontSize", fixes);
        return tw with { Width = width, FontSize = fontSize };
    }

    private static HistorySettings NormalizeHistory(HistorySettings history, ICollection<string> fixes)
    {
        // N9
        var maxEntries = Clamp(history.MaxEntries, 100, 100000, "History.MaxEntries", fixes);
        return history with { MaxEntries = maxEntries };
    }

    private static NetworkSettings NormalizeNetwork(NetworkSettings network, ICollection<string> fixes)
    {
        // N10
        if (network.ProxyMode == ProxyMode.Custom)
        {
            var isValid = !string.IsNullOrWhiteSpace(network.CustomProxyUrl) &&
                          Uri.TryCreate(network.CustomProxyUrl, UriKind.Absolute, out var uri) &&
                          (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase));

            if (!isValid)
            {
                fixes.Add("Network.ProxyMode: Custom → System（代理地址无效，已隐藏）");
                return network with { ProxyMode = ProxyMode.System };
            }
        }

        return network;
    }

    private static int Clamp(int value, int min, int max, string propertyPath, ICollection<string> fixes)
    {
        if (value < min)
        {
            fixes.Add($"{propertyPath}: {value} → {min}");
            return min;
        }

        if (value > max)
        {
            fixes.Add($"{propertyPath}: {value} → {max}");
            return max;
        }

        return value;
    }

    private static double Clamp(double value, double min, double max, string propertyPath, ICollection<string> fixes)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < min)
        {
            fixes.Add(FormattableString.Invariant($"{propertyPath}: {value} → {min}"));
            return min;
        }

        if (value > max)
        {
            fixes.Add(FormattableString.Invariant($"{propertyPath}: {value} → {max}"));
            return max;
        }

        return value;
    }
}
