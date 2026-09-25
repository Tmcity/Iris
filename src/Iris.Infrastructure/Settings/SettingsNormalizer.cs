using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Iris.Core.Actions;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Core.Translation;

namespace Iris.Infrastructure.Settings;

/// <summary>
/// 设置规范化器：纯函数，保证 AppSettings 永远满足 N1–N14 不变量。
/// 若未发生任何修复，返回原实例。
/// </summary>
internal static class SettingsNormalizer
{
    private static readonly string[] AllowedLogLevels = ["Verbose", "Debug", "Information", "Warning", "Error"];
    private static readonly string[] AllowedLanguages = ["zh-CN", "en-US"];

    public static AppSettings Normalize(AppSettings input, ICollection<string> fixes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(fixes);

        var initialFixCount = fixes.Count;

        // N1 顶层节与 SchemaVersion
        var schemaVersion = input.SchemaVersion;
        if (schemaVersion != AppSettings.CurrentSchemaVersion)
        {
            fixes.Add(FormattableString.Invariant($"SchemaVersion: {schemaVersion} → {AppSettings.CurrentSchemaVersion}"));
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
        // N11: UiLanguage
        var uiLang = general.UiLanguage;
        var canonicalLang = AllowedLanguages.FirstOrDefault(l => string.Equals(l, uiLang, StringComparison.OrdinalIgnoreCase));
        if (canonicalLang == null)
        {
            canonicalLang = "zh-CN";
            fixes.Add($"General.UiLanguage: {uiLang ?? "null"} → zh-CN");
        }
        else if (!string.Equals(uiLang, canonicalLang, StringComparison.Ordinal))
        {
            fixes.Add($"General.UiLanguage: {uiLang} → {canonicalLang}");
        }

        // N11: LogLevel
        var logLevel = general.LogLevel;
        var canonicalLevel = AllowedLogLevels.FirstOrDefault(l => string.Equals(l, logLevel, StringComparison.OrdinalIgnoreCase));
        if (canonicalLevel == null)
        {
            canonicalLevel = "Information";
            fixes.Add($"General.LogLevel: {logLevel ?? "null"} → Information");
        }
        else if (!string.Equals(logLevel, canonicalLevel, StringComparison.Ordinal))
        {
            fixes.Add($"General.LogLevel: {logLevel} → {canonicalLevel}");
        }

        // N12: Theme
        var theme = general.Theme;
        if (!Enum.IsDefined(theme))
        {
            fixes.Add(FormattableString.Invariant($"General.Theme: {(int)theme} → {AppTheme.System}"));
            theme = AppTheme.System;
        }

        if (string.Equals(general.UiLanguage, canonicalLang, StringComparison.Ordinal) &&
            string.Equals(general.LogLevel, canonicalLevel, StringComparison.Ordinal) &&
            general.Theme == theme)
        {
            return general;
        }

        return general with
        {
            UiLanguage = canonicalLang,
            LogLevel = canonicalLevel,
            Theme = theme,
        };
    }

    private static Dictionary<IrisAction, HotkeyBinding> NormalizeHotkeys(
        IReadOnlyDictionary<IrisAction, HotkeyBinding>? hotkeys,
        ICollection<string> fixes)
    {
        // N2, N12, N13
        var defaults = HotkeyDefaults.Create();
        var result = new Dictionary<IrisAction, HotkeyBinding>();
        const HotkeyModifiers allModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win;

        if (hotkeys != null)
        {
            foreach (var (action, binding) in hotkeys)
            {
                // N12: 未定义动作
                if (!Enum.IsDefined(action))
                {
                    fixes.Add(FormattableString.Invariant($"Hotkeys: 未定义动作 {(int)action} → 已移除"));
                    continue;
                }

                // N2 & D-3: 值为 null 的条目
                if (binding == null)
                {
                    if (defaults.TryGetValue(action, out var defaultBinding))
                    {
                        result[action] = defaultBinding;
                        fixes.Add($"Hotkeys[{action}]: null → 默认绑定");
                    }
                    else
                    {
                        fixes.Add($"Hotkeys[{action}]: null → 已移除");
                    }

                    continue;
                }

                var currentBinding = binding;

                // N12: Modifiers 只保留已定义位
                var masked = currentBinding.Modifiers & allModifiers;
                if (masked != currentBinding.Modifiers)
                {
                    fixes.Add(FormattableString.Invariant($"Hotkeys[{action}].Modifiers: 0x{(int)currentBinding.Modifiers:X} → 0x{(int)masked:X}"));
                    currentBinding = currentBinding with { Modifiers = masked };
                }

                // N2: Key 为空白
                if (string.IsNullOrWhiteSpace(currentBinding.Key))
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
                    result[action] = currentBinding;
                }
            }
        }

        // N2: 补齐缺失的默认动作
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
            fixes.Add($"Translation.PrimaryTargetLanguage: {primaryTarget ?? "null"} → {LanguageCodes.ChineseSimplified}");
            primaryTarget = LanguageCodes.ChineseSimplified;
        }

        var secondaryTarget = translation.SecondaryTargetLanguage;
        if (string.IsNullOrWhiteSpace(secondaryTarget) || string.Equals(secondaryTarget, "auto", StringComparison.OrdinalIgnoreCase))
        {
            fixes.Add($"Translation.SecondaryTargetLanguage: {secondaryTarget ?? "null"} → {LanguageCodes.English}");
            secondaryTarget = LanguageCodes.English;
        }

        var defaultSource = translation.DefaultSourceLanguage;
        if (string.IsNullOrWhiteSpace(defaultSource))
        {
            fixes.Add($"Translation.DefaultSourceLanguage: {defaultSource ?? "null"} → {LanguageCodes.Auto}");
            defaultSource = LanguageCodes.Auto;
        }

        // N12: AutoCopy & SelectionLineBreaks
        var autoCopy = translation.AutoCopy;
        if (!Enum.IsDefined(autoCopy))
        {
            fixes.Add(FormattableString.Invariant($"Translation.AutoCopy: {(int)autoCopy} → {AutoCopyMode.None}"));
            autoCopy = AutoCopyMode.None;
        }

        var lineBreaks = translation.SelectionLineBreaks;
        if (!Enum.IsDefined(lineBreaks))
        {
            fixes.Add(FormattableString.Invariant($"Translation.SelectionLineBreaks: {(int)lineBreaks} → {LineBreakHandling.Keep}"));
            lineBreaks = LineBreakHandling.Keep;
        }

        // CustomPrompts 规范化 (N3, N13)
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
            if (prompt == null)
            {
                fixes.Add("Translation.CustomPrompts: 移除 null 模板");
                continue;
            }

            if (string.IsNullOrWhiteSpace(prompt.Id) ||
                string.Equals(prompt.Id, BuiltInPrompts.DefaultTemplateId, StringComparison.OrdinalIgnoreCase) ||
                !seenPromptIds.Add(prompt.Id))
            {
                fixes.Add($"Translation.CustomPrompts[{prompt.Id}]: 非法或与内置模板冲突 → 已移除");
                continue;
            }

            // N13: System 或 User 为 null 或空白 → 移除该模板
            if (string.IsNullOrWhiteSpace(prompt.System) || string.IsNullOrWhiteSpace(prompt.User))
            {
                fixes.Add($"Translation.CustomPrompts[{prompt.Id}]: System 或 User 为空 → 已移除");
                continue;
            }

            // N13: Name 为空白 → 用 Id
            var name = prompt.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                fixes.Add($"Translation.CustomPrompts[{prompt.Id}].Name: 空白 → {prompt.Id}");
                name = prompt.Id;
            }

            normalizedPrompts.Add(prompt with { Name = name });
        }

        // DefaultPromptTemplateId 必须存在于内置模板或自定义模板中，且 N14 不区分大小写匹配后改写为精确 Id
        var defaultPromptId = translation.DefaultPromptTemplateId;
        string? canonicalPromptId = null;

        if (string.Equals(defaultPromptId, BuiltInPrompts.DefaultTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            canonicalPromptId = BuiltInPrompts.DefaultTemplateId;
        }
        else
        {
            var matchedCustom = normalizedPrompts.FirstOrDefault(p => string.Equals(p.Id, defaultPromptId, StringComparison.OrdinalIgnoreCase));
            if (matchedCustom != null)
            {
                canonicalPromptId = matchedCustom.Id;
            }
        }

        if (canonicalPromptId == null)
        {
            fixes.Add($"Translation.DefaultPromptTemplateId: {defaultPromptId ?? "null"} → {BuiltInPrompts.DefaultTemplateId}");
            defaultPromptId = BuiltInPrompts.DefaultTemplateId;
        }
        else if (!string.Equals(defaultPromptId, canonicalPromptId, StringComparison.Ordinal))
        {
            fixes.Add($"Translation.DefaultPromptTemplateId: {defaultPromptId} → {canonicalPromptId}");
            defaultPromptId = canonicalPromptId;
        }

        // Providers 规范化 (N3, N12, N13, N14)
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
            if (profile == null)
            {
                fixes.Add("Translation.Providers: 移除 null Profile");
                continue;
            }

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

            // N12: 未定义 Kind 移除 Profile
            if (!Enum.IsDefined(profile.Kind))
            {
                fixes.Add(FormattableString.Invariant($"Translation.Providers: 移除未定义 Kind {(int)profile.Kind} 的 Profile '{profile.Id}'"));
                continue;
            }

            var displayName = profile.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                fixes.Add($"Translation.Providers[{profile.Id}].DisplayName: 空白 → {profile.Id}");
                displayName = profile.Id;
            }

            // N13: Model 为 null → 空字符串，并设 Enabled = false
            var model = profile.Model;
            var enabled = profile.Enabled;
            if (model == null)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].Model: null → \"\"（并禁用）");
                model = string.Empty;
                enabled = false;
            }

            var timeout = Clamp(profile.TimeoutSeconds, 5, 600, $"Translation.Providers[{profile.Id}].TimeoutSeconds", fixes);

            int? maxOutputTokens = profile.MaxOutputTokens;
            if (maxOutputTokens.HasValue && maxOutputTokens.Value < 1)
            {
                fixes.Add(FormattableString.Invariant($"Translation.Providers[{profile.Id}].MaxOutputTokens: {maxOutputTokens.Value} → null"));
                maxOutputTokens = null;
            }

            double? temp = profile.Temperature;
            if (temp.HasValue)
            {
                var clampedTemp = Clamp(temp.Value, 0.0, 2.0, $"Translation.Providers[{profile.Id}].Temperature", fixes);
                temp = clampedTemp;
            }

            if (profile.Kind == TranslationProviderKind.OpenAICompatible && string.IsNullOrWhiteSpace(profile.BaseUrl) && enabled)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].Enabled: true → false（缺少 BaseUrl）");
                enabled = false;
            }

            var openAIOptions = profile.OpenAIOptions;
            if (profile.Kind == TranslationProviderKind.OpenAICompatible)
            {
                if (openAIOptions == null)
                {
                    fixes.Add($"Translation.Providers[{profile.Id}].OpenAIOptions: null → 默认值");
                    openAIOptions = new OpenAICompatibleOptions();
                }

                // N12: MaxTokensField
                if (!Enum.IsDefined(openAIOptions.MaxTokensField))
                {
                    fixes.Add(FormattableString.Invariant($"Translation.Providers[{profile.Id}].OpenAIOptions.MaxTokensField: {(int)openAIOptions.MaxTokensField} → {MaxTokensField.MaxTokens}"));
                    openAIOptions = openAIOptions with { MaxTokensField = MaxTokensField.MaxTokens };
                }

                // N13: ChatCompletionsPath 为 null 或空白 → 默认值
                if (string.IsNullOrWhiteSpace(openAIOptions.ChatCompletionsPath))
                {
                    fixes.Add($"Translation.Providers[{profile.Id}].OpenAIOptions.ChatCompletionsPath: 为空 → 默认值");
                    openAIOptions = openAIOptions with { ChatCompletionsPath = "chat/completions" };
                }
            }

            var anthropicOptions = profile.AnthropicOptions;
            if (profile.Kind == TranslationProviderKind.Anthropic && anthropicOptions == null)
            {
                fixes.Add($"Translation.Providers[{profile.Id}].AnthropicOptions: null → 默认值");
                anthropicOptions = new AnthropicOptions();
            }

            // N14: PromptTemplateId 规范化
            var promptTemplateId = profile.PromptTemplateId;
            if (promptTemplateId != null)
            {
                string? matchedTemplateId = null;
                if (string.Equals(promptTemplateId, BuiltInPrompts.DefaultTemplateId, StringComparison.OrdinalIgnoreCase))
                {
                    matchedTemplateId = BuiltInPrompts.DefaultTemplateId;
                }
                else
                {
                    var matchedCustom = normalizedPrompts.FirstOrDefault(p => string.Equals(p.Id, promptTemplateId, StringComparison.OrdinalIgnoreCase));
                    if (matchedCustom != null)
                    {
                        matchedTemplateId = matchedCustom.Id;
                    }
                }

                if (matchedTemplateId == null)
                {
                    fixes.Add($"Translation.Providers[{profile.Id}].PromptTemplateId: {promptTemplateId} → null");
                    promptTemplateId = null;
                }
                else if (!string.Equals(promptTemplateId, matchedTemplateId, StringComparison.Ordinal))
                {
                    fixes.Add($"Translation.Providers[{profile.Id}].PromptTemplateId: {promptTemplateId} → {matchedTemplateId}");
                    promptTemplateId = matchedTemplateId;
                }
            }

            normalizedProviders.Add(profile with
            {
                DisplayName = displayName,
                Model = model,
                TimeoutSeconds = timeout,
                MaxOutputTokens = maxOutputTokens,
                Temperature = temp,
                Enabled = enabled,
                OpenAIOptions = openAIOptions,
                AnthropicOptions = anthropicOptions,
                PromptTemplateId = promptTemplateId,
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
            AutoCopy = autoCopy,
            SelectionLineBreaks = lineBreaks,
            CustomPrompts = normalizedPrompts,
            DefaultPromptTemplateId = defaultPromptId,
            Providers = normalizedProviders,
        };
    }

    private static OcrSettings NormalizeOcr(OcrSettings ocr, ICollection<string> fixes)
    {
        // N4, N12, N13, N14
        var defaultOcrProviders = new OcrSettings().Providers;
        var rawProviders = ocr.Providers;
        if (rawProviders == null)
        {
            fixes.Add("Ocr.Providers: null → 默认列表");
            rawProviders = defaultOcrProviders;
        }

        // N12: TextLayout
        var textLayout = ocr.TextLayout;
        if (!Enum.IsDefined(textLayout))
        {
            fixes.Add(FormattableString.Invariant($"Ocr.TextLayout: {(int)textLayout} → {OcrTextLayout.MergeParagraphs}"));
            textLayout = OcrTextLayout.MergeParagraphs;
        }

        var normalizedProviders = new List<OcrProviderProfile>();
        var seenProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in rawProviders)
        {
            if (profile == null)
            {
                fixes.Add("Ocr.Providers: 移除 null Profile");
                continue;
            }

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

            // N12: 未定义 Kind 移除 Profile
            if (!Enum.IsDefined(profile.Kind))
            {
                fixes.Add(FormattableString.Invariant($"Ocr.Providers: 移除未定义 Kind {(int)profile.Kind} 的 Profile '{profile.Id}'"));
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
            if (profile.Kind == OcrProviderKind.BaiduCloud)
            {
                if (baiduOptions == null)
                {
                    fixes.Add($"Ocr.Providers[{profile.Id}].BaiduOptions: null → 默认值");
                    baiduOptions = new BaiduOcrOptions();
                }

                // N12: BaiduOcrOptions.Mode
                if (!Enum.IsDefined(baiduOptions.Mode))
                {
                    fixes.Add(FormattableString.Invariant($"Ocr.Providers[{profile.Id}].BaiduOptions.Mode: {(int)baiduOptions.Mode} → {BaiduOcrMode.AccurateBasic}"));
                    baiduOptions = baiduOptions with { Mode = BaiduOcrMode.AccurateBasic };
                }

                // N13: BaseUrl 为空 → 默认值
                if (string.IsNullOrWhiteSpace(baiduOptions.BaseUrl))
                {
                    fixes.Add($"Ocr.Providers[{profile.Id}].BaiduOptions.BaseUrl: 为空 → 默认值");
                    baiduOptions = baiduOptions with { BaseUrl = "https://aip.baidubce.com" };
                }
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

        // N4 & N14: 补齐缺失的内置 Profile（直接复制 new OcrSettings().Providers 中的对应项，不硬编码）
        if (!normalizedProviders.Any(p => string.Equals(p.Id, BuiltInProviderIds.BaiduOcr, StringComparison.OrdinalIgnoreCase)))
        {
            fixes.Add("Ocr.Providers: 缺少内置 ocr-baidu → 已追加默认配置");
            var defaultBaidu = defaultOcrProviders.First(p => string.Equals(p.Id, BuiltInProviderIds.BaiduOcr, StringComparison.OrdinalIgnoreCase));
            normalizedProviders.Add(defaultBaidu);
        }

        if (!normalizedProviders.Any(p => string.Equals(p.Id, BuiltInProviderIds.WindowsOcr, StringComparison.OrdinalIgnoreCase)))
        {
            fixes.Add("Ocr.Providers: 缺少内置 ocr-windows → 已追加默认配置");
            var defaultWindows = defaultOcrProviders.First(p => string.Equals(p.Id, BuiltInProviderIds.WindowsOcr, StringComparison.OrdinalIgnoreCase));
            normalizedProviders.Add(defaultWindows);
        }

        // N4 & N14: ActiveProviderId
        var activeId = ocr.ActiveProviderId;
        var matchedActive = normalizedProviders.FirstOrDefault(p => string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
        if (matchedActive == null)
        {
            fixes.Add($"Ocr.ActiveProviderId: {activeId ?? "null"} → {BuiltInProviderIds.BaiduOcr}");
            activeId = BuiltInProviderIds.BaiduOcr;
        }
        else if (!string.Equals(activeId, matchedActive.Id, StringComparison.Ordinal))
        {
            fixes.Add($"Ocr.ActiveProviderId: {activeId} → {matchedActive.Id}");
            activeId = matchedActive.Id;
        }

        // N4 & N14: FallbackProviderId
        var fallbackId = ocr.FallbackProviderId;
        if (fallbackId != null)
        {
            var matchedFallback = normalizedProviders.FirstOrDefault(p => string.Equals(p.Id, fallbackId, StringComparison.OrdinalIgnoreCase));
            if (matchedFallback == null || string.Equals(matchedFallback.Id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                fixes.Add($"Ocr.FallbackProviderId: {fallbackId} → null");
                fallbackId = null;
            }
            else if (!string.Equals(fallbackId, matchedFallback.Id, StringComparison.Ordinal))
            {
                fixes.Add($"Ocr.FallbackProviderId: {fallbackId} → {matchedFallback.Id}");
                fallbackId = matchedFallback.Id;
            }
        }

        var langHint = ocr.LanguageHint;
        if (string.IsNullOrWhiteSpace(langHint))
        {
            fixes.Add($"Ocr.LanguageHint: {langHint ?? "null"} → {LanguageCodes.Auto}");
            langHint = LanguageCodes.Auto;
        }

        return ocr with
        {
            TextLayout = textLayout,
            Providers = normalizedProviders,
            ActiveProviderId = activeId,
            FallbackProviderId = fallbackId,
            LanguageHint = langHint,
        };
    }

    private static SelectionSettings NormalizeSelection(SelectionSettings selection, ICollection<string> fixes)
    {
        // N12: MouseMode
        var mouseMode = selection.MouseMode;
        if (!Enum.IsDefined(mouseMode))
        {
            fixes.Add(FormattableString.Invariant($"Selection.MouseMode: {(int)mouseMode} → {MouseSelectionMode.Off}"));
            mouseMode = MouseSelectionMode.Off;
        }

        // N5 & N12: ReadOrder
        var rawReadOrder = selection.ReadOrder;
        IReadOnlyList<SelectionReadMethod> readOrder;
        if (rawReadOrder == null || rawReadOrder.Count == 0)
        {
            fixes.Add("Selection.ReadOrder: 空白 → 默认顺序");
            readOrder = [SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard];
        }
        else
        {
            var validList = new List<SelectionReadMethod>();
            foreach (var m in rawReadOrder)
            {
                if (!Enum.IsDefined(m))
                {
                    fixes.Add(FormattableString.Invariant($"Selection.ReadOrder: 移除未定义元素 {(int)m}"));
                    continue;
                }

                if (!validList.Contains(m))
                {
                    validList.Add(m);
                }
                else
                {
                    fixes.Add("Selection.ReadOrder: 存在重复项 → 已去重");
                }
            }

            if (validList.Count == 0)
            {
                fixes.Add("Selection.ReadOrder: 空白 → 默认顺序");
                validList = [SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard];
            }

            readOrder = validList;
        }

        var clipboardTimeout = Clamp(selection.ClipboardTimeoutMs, 100, 2000, "Selection.ClipboardTimeoutMs", fixes);
        var minDrag = Clamp(selection.MinDragDistancePx, 2, 50, "Selection.MinDragDistancePx", fixes);
        var autoHide = Clamp(selection.IconAutoHideMs, 500, 10000, "Selection.IconAutoHideMs", fixes);

        // N5 & N13: ExcludedProcesses
        var rawExcluded = selection.ExcludedProcesses;
        IReadOnlyList<string> excluded;
        if (rawExcluded == null)
        {
            fixes.Add("Selection.ExcludedProcesses: null → []");
            excluded = [];
        }
        else
        {
            var cleaned = new List<string>();
            foreach (var p in rawExcluded)
            {
                if (p == null)
                {
                    fixes.Add("Selection.ExcludedProcesses: 移除 null 元素");
                }
                else
                {
                    cleaned.Add(p);
                }
            }

            excluded = cleaned;
        }

        // N5 & N13: ClipboardUnsafeProcesses
        var rawUnsafe = selection.ClipboardUnsafeProcesses;
        IReadOnlyList<string> unsafeProcesses;
        if (rawUnsafe == null)
        {
            fixes.Add("Selection.ClipboardUnsafeProcesses: null → 默认列表");
            unsafeProcesses = new SelectionSettings().ClipboardUnsafeProcesses;
        }
        else
        {
            var cleaned = new List<string>();
            foreach (var p in rawUnsafe)
            {
                if (p == null)
                {
                    fixes.Add("Selection.ClipboardUnsafeProcesses: 移除 null 元素");
                }
                else
                {
                    cleaned.Add(p);
                }
            }

            unsafeProcesses = cleaned;
        }

        return selection with
        {
            MouseMode = mouseMode,
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
        // N7 & N12
        var clickAction = fb.ClickAction;
        if (!Enum.IsDefined(clickAction))
        {
            fixes.Add(FormattableString.Invariant($"FloatingBall.ClickAction: {(int)clickAction} → {IrisAction.ScreenshotTranslate}"));
            clickAction = IrisAction.ScreenshotTranslate;
        }

        var size = Clamp(fb.Size, 24.0, 96.0, "FloatingBall.Size", fixes);
        var opacity = Clamp(fb.Opacity, 0.2, 1.0, "FloatingBall.Opacity", fixes);
        return fb with
        {
            ClickAction = clickAction,
            Size = size,
            Opacity = opacity,
        };
    }

    private static TranslateWindowSettings NormalizeTranslateWindow(TranslateWindowSettings tw, ICollection<string> fixes)
    {
        // N8 & N12
        var placement = tw.Placement;
        if (!Enum.IsDefined(placement))
        {
            fixes.Add(FormattableString.Invariant($"TranslateWindow.Placement: {(int)placement} → {WindowPlacementMode.FollowCursor}"));
            placement = WindowPlacementMode.FollowCursor;
        }

        var width = Clamp(tw.Width, 280.0, 1200.0, "TranslateWindow.Width", fixes);
        var fontSize = Clamp(tw.FontSize, 10.0, 32.0, "TranslateWindow.FontSize", fixes);
        return tw with
        {
            Placement = placement,
            Width = width,
            FontSize = fontSize,
        };
    }

    private static HistorySettings NormalizeHistory(HistorySettings history, ICollection<string> fixes)
    {
        // N9
        var maxEntries = Clamp(history.MaxEntries, 100, 100000, "History.MaxEntries", fixes);
        return history with { MaxEntries = maxEntries };
    }

    private static NetworkSettings NormalizeNetwork(NetworkSettings network, ICollection<string> fixes)
    {
        // N10 & N12
        var mode = network.ProxyMode;
        if (!Enum.IsDefined(mode))
        {
            fixes.Add(FormattableString.Invariant($"Network.ProxyMode: {(int)mode} → {ProxyMode.System}"));
            mode = ProxyMode.System;
        }

        if (mode == ProxyMode.Custom)
        {
            var isValid = !string.IsNullOrWhiteSpace(network.CustomProxyUrl) &&
                          Uri.TryCreate(network.CustomProxyUrl, UriKind.Absolute, out var uri) &&
                          (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase));

            if (!isValid)
            {
                fixes.Add("Network.ProxyMode: Custom → System（代理地址无效，已隐藏）");
                mode = ProxyMode.System;
            }
        }

        return network with { ProxyMode = mode };
    }

    private static int Clamp(int value, int min, int max, string propertyPath, ICollection<string> fixes)
    {
        if (value < min)
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
