using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Core.Actions;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class SettingsNormalizerTests
{
    [Fact]
    public void Defaults_AreFixedPoint()
    {
        var defaults = new AppSettings();
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(defaults, fixes);

        fixes.ShouldBeEmpty();
        normalized.ShouldBeSameAs(defaults);
    }

    [Fact]
    public void SpecExample_IsFixedPoint()
    {
        var examplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.example.json");
        var json = File.ReadAllText(examplePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, IrisJson.Options)!;
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        fixes.ShouldBeEmpty();
        normalized.ShouldBeSameAs(settings);
    }

    [Fact]
    public void N1_NullSectionsAndOldSchemaVersion_NormalizedToDefaults()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            General = null!,
            Translation = null!,
            Ocr = null!,
            Selection = null!,
            Screenshot = null!,
            FloatingBall = null!,
            TranslateWindow = null!,
            History = null!,
            Network = null!,
            Hotkeys = null!,
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        normalized.General.ShouldNotBeNull();
        normalized.Translation.ShouldNotBeNull();
        normalized.Ocr.ShouldNotBeNull();
        normalized.Selection.ShouldNotBeNull();
        normalized.Screenshot.ShouldNotBeNull();
        normalized.FloatingBall.ShouldNotBeNull();
        normalized.TranslateWindow.ShouldNotBeNull();
        normalized.History.ShouldNotBeNull();
        normalized.Network.ShouldNotBeNull();
        normalized.Hotkeys.ShouldNotBeNull();

        fixes.ShouldContain("SchemaVersion: 0 → 1");
        fixes.ShouldContain("General: null → 默认值");
        fixes.ShouldContain("Hotkeys: null → 默认值");
    }

    [Fact]
    public void N2_Hotkeys_MissingActionOrBlankKey_Normalized()
    {
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.SelectionTranslate] = new(HotkeyModifiers.Control, "  "), // blank key, has default
            [IrisAction.OpenSettings] = new(HotkeyModifiers.Control, "  "),       // blank key, no default
            [IrisAction.InputTranslate] = null!,                                  // null entry
        };
        var settings = new AppSettings { Hotkeys = hotkeys };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.Hotkeys[IrisAction.SelectionTranslate].Key.ShouldBe("D");
        normalized.Hotkeys.ContainsKey(IrisAction.OpenSettings).ShouldBeFalse();
        normalized.Hotkeys[IrisAction.InputTranslate].Key.ShouldBe("F");
        normalized.Hotkeys.ContainsKey(IrisAction.ScreenshotTranslate).ShouldBeTrue(); // missing default added

        fixes.ShouldContain(f => f.StartsWith("Hotkeys[SelectionTranslate]:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("Hotkeys[OpenSettings]:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("Hotkeys[InputTranslate]:", StringComparison.Ordinal));
    }

    [Fact]
    public void N3_Translation_ProvidersDuplicateIdAndBlankDisplayName_Normalized()
    {
        var p1 = new TranslationProviderProfile
        {
            Id = "p1",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "", // blank display name
            Model = "gpt-4o",
            TimeoutSeconds = 1000, // over 600
            MaxOutputTokens = -5, // < 1
            Temperature = 3.5, // > 2
            BaseUrl = "", // blank baseUrl
            Enabled = true,
            OpenAIOptions = null, // null options
        };
        var p2Duplicate = new TranslationProviderProfile
        {
            Id = "P1", // duplicate case-insensitive
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Duplicate",
            Model = "gpt-4o",
        };
        var pBlankId = new TranslationProviderProfile
        {
            Id = "   ",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "BlankId",
            Model = "gpt-4o",
        };

        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers = [p1, p2Duplicate, pBlankId],
                MaxConcurrentProviders = 0,
                MaxInputChars = 50,
                CacheCapacity = -10,
                PrimaryTargetLanguage = "auto",
                SecondaryTargetLanguage = "",
                DefaultSourceLanguage = "",
            },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        var normTrans = normalized.Translation;
        normTrans.Providers.Count.ShouldBe(1);
        normTrans.Providers[0].DisplayName.ShouldBe("p1");
        normTrans.Providers[0].TimeoutSeconds.ShouldBe(600);
        normTrans.Providers[0].MaxOutputTokens.ShouldBeNull();
        normTrans.Providers[0].Temperature.ShouldBe(2.0);
        normTrans.Providers[0].Enabled.ShouldBeFalse();
        normTrans.Providers[0].OpenAIOptions.ShouldNotBeNull();

        normTrans.MaxConcurrentProviders.ShouldBe(1);
        normTrans.MaxInputChars.ShouldBe(100);
        normTrans.CacheCapacity.ShouldBe(0);
        normTrans.PrimaryTargetLanguage.ShouldBe(LanguageCodes.ChineseSimplified);
        normTrans.SecondaryTargetLanguage.ShouldBe(LanguageCodes.English);
        normTrans.DefaultSourceLanguage.ShouldBe(LanguageCodes.Auto);
    }

    [Fact]
    public void N3_Translation_CustomPromptsConflictAndMissingDefaultPromptId_Normalized()
    {
        var conflictPrompt = new PromptTemplate
        {
            Id = BuiltInPrompts.DefaultTemplateId, // conflict with builtin
            Name = "Conflict",
            System = "sys",
            User = "usr",
        };
        var custom1 = new PromptTemplate
        {
            Id = "custom.1",
            Name = "Custom 1",
            System = "sys",
            User = "usr",
        };
        var duplicateCustom = new PromptTemplate
        {
            Id = "CUSTOM.1", // duplicate
            Name = "Duplicate",
            System = "sys",
            User = "usr",
        };

        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                CustomPrompts = [conflictPrompt, custom1, duplicateCustom],
                DefaultPromptTemplateId = "non_existent_template",
            },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.Translation.CustomPrompts.Count.ShouldBe(1);
        normalized.Translation.CustomPrompts[0].Id.ShouldBe("custom.1");
        normalized.Translation.DefaultPromptTemplateId.ShouldBe(BuiltInPrompts.DefaultTemplateId);

        fixes.ShouldContain(f => f.Contains("Translation.CustomPrompts"));
        fixes.ShouldContain(f => f.Contains("Translation.DefaultPromptTemplateId"));
    }

    [Fact]
    public void N4_Ocr_MissingBuiltInsAndActiveFallbackNormalized()
    {
        // Only one custom ocr profile, missing built-in ocr-baidu and ocr-windows
        var customOcr = new OcrProviderProfile
        {
            Id = "my-custom-ocr",
            Kind = OcrProviderKind.BaiduCloud,
            DisplayName = "Custom",
            TimeoutSeconds = 200, // > 120
            BaiduOptions = null,
        };

        var settings = new AppSettings
        {
            Ocr = new OcrSettings
            {
                Providers = [customOcr],
                ActiveProviderId = "non-existent-active",
                FallbackProviderId = "ocr-baidu", // will become equal to active when active resets to ocr-baidu
                LanguageHint = "  ",
            },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        var normOcr = normalized.Ocr;
        normOcr.Providers.Count.ShouldBe(3);
        normOcr.Providers.ShouldContain(p => p.Id == BuiltInProviderIds.BaiduOcr);
        normOcr.Providers.ShouldContain(p => p.Id == BuiltInProviderIds.WindowsOcr);

        normOcr.ActiveProviderId.ShouldBe(BuiltInProviderIds.BaiduOcr);
        // Fallback was ocr-baidu which equals ActiveProviderId -> resets to null
        normOcr.FallbackProviderId.ShouldBeNull();
        normOcr.LanguageHint.ShouldBe(LanguageCodes.Auto);
    }

    [Fact]
    public void N5_Selection_DuplicateReadOrderAndNullLists_Normalized()
    {
        var settings = new AppSettings
        {
            Selection = new SelectionSettings
            {
                ReadOrder = [SelectionReadMethod.Clipboard, SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation],
                ClipboardTimeoutMs = 50, // < 100
                MinDragDistancePx = 100, // > 50
                IconAutoHideMs = 20000,  // > 10000
                ExcludedProcesses = null!,
                ClipboardUnsafeProcesses = null!,
            },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        var sel = normalized.Selection;
        sel.ReadOrder.ShouldBe([SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation]);
        sel.ClipboardTimeoutMs.ShouldBe(100);
        sel.MinDragDistancePx.ShouldBe(50);
        sel.IconAutoHideMs.ShouldBe(10000);
        sel.ExcludedProcesses.ShouldNotBeNull();
        sel.ClipboardUnsafeProcesses.ShouldNotBeNull();
    }

    [Fact]
    public void N6_N9_ClampingAndNaNInfinity_ClampsToLowerBound()
    {
        var settings = new AppSettings
        {
            Screenshot = new ScreenshotSettings { MaskOpacity = double.NaN },
            FloatingBall = new FloatingBallSettings { Size = double.PositiveInfinity, Opacity = -1.0 },
            TranslateWindow = new TranslateWindowSettings { Width = 200, FontSize = 40 },
            History = new HistorySettings { MaxEntries = 20 },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.Screenshot.MaskOpacity.ShouldBe(0.0);
        normalized.FloatingBall.Size.ShouldBe(24.0); // clamped to lower bound for Infinity
        normalized.FloatingBall.Opacity.ShouldBe(0.2);
        normalized.TranslateWindow.Width.ShouldBe(280.0);
        normalized.TranslateWindow.FontSize.ShouldBe(32.0);
        normalized.History.MaxEntries.ShouldBe(100);
    }

    [Fact]
    public void N10_Network_InvalidCustomProxyUrl_MasksUrlInFixMessage()
    {
        const string badUrl = "http://user:secret_pass@bad host:invalid/";
        var settings = new AppSettings
        {
            Network = new NetworkSettings
            {
                ProxyMode = ProxyMode.Custom,
                CustomProxyUrl = badUrl,
            },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.Network.ProxyMode.ShouldBe(ProxyMode.System);
        fixes.Count.ShouldBe(1);
        fixes[0].ShouldBe("Network.ProxyMode: Custom → System（代理地址无效，已隐藏）");
        // MUST NOT contain the secret password or URL
        fixes[0].ShouldNotContain("secret_pass");
        fixes[0].ShouldNotContain(badUrl);
    }

    [Fact]
    public void N11_General_UiLanguageAndLogLevel_NormalizedToCanonical()
    {
        var settings = new AppSettings
        {
            General = new GeneralSettings
            {
                UiLanguage = "zh-cn",
                LogLevel = "debug",
            },
        };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.General.UiLanguage.ShouldBe("zh-CN");
        normalized.General.LogLevel.ShouldBe("Debug");

        var badSettings = new AppSettings
        {
            General = new GeneralSettings
            {
                UiLanguage = "fr-FR",
                LogLevel = "SuperVerbose",
            },
        };
        var badFixes = new List<string>();

        var badNormalized = SettingsNormalizer.Normalize(badSettings, badFixes);

        badNormalized.General.UiLanguage.ShouldBe("zh-CN");
        badNormalized.General.LogLevel.ShouldBe("Information");
    }

    [Fact]
    public void NoFixes_ReturnsOriginalInstance_ReferenceEquals()
    {
        var settings = new AppSettings();
        var fixes = new List<string>();

        var result = SettingsNormalizer.Normalize(settings, fixes);

        fixes.ShouldBeEmpty();
        ReferenceEquals(result, settings).ShouldBeTrue();
    }

    [Fact]
    public void N2_a_OpenSettings_Null_RemovedWithFix()
    {
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.OpenSettings] = null!,
        };
        var settings = new AppSettings { Hotkeys = hotkeys };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.Hotkeys.ContainsKey(IrisAction.OpenSettings).ShouldBeFalse();
        fixes.ShouldContain("Hotkeys[OpenSettings]: null → 已移除");
    }

    [Fact]
    public void N2_b_InputTranslate_Null_ReplacedWithDefaultAndFix()
    {
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = null!,
        };
        var settings = new AppSettings { Hotkeys = hotkeys };
        var fixes = new List<string>();

        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        normalized.Hotkeys[IrisAction.InputTranslate].ShouldBe(HotkeyDefaults.Create()[IrisAction.InputTranslate]);
        fixes.ShouldContain("Hotkeys[InputTranslate]: null → 默认绑定");
    }

    [Fact]
    public void N12_AllEnumFields_NormalizedCorrectly()
    {
        var settings = new AppSettings
        {
            General = new GeneralSettings { Theme = (AppTheme)99 },
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [(IrisAction)999] = new(HotkeyModifiers.Control, "K"),
                [IrisAction.InputTranslate] = new((HotkeyModifiers)0x16, "F"),
            },
            Translation = new TranslationSettings
            {
                AutoCopy = (AutoCopyMode)99,
                SelectionLineBreaks = (LineBreakHandling)99,
                Providers =
                [
                    new()
                    {
                        Id = "tp-invalid-kind",
                        Kind = (TranslationProviderKind)99,
                        DisplayName = "Invalid Kind",
                        Model = "gpt-4",
                    },
                    new()
                    {
                        Id = "tp-valid",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "Valid",
                        Model = "gpt-4",
                        BaseUrl = "https://api.openai.com/v1",
                        OpenAIOptions = new() { MaxTokensField = (MaxTokensField)99 },
                    },
                ],
            },
            Ocr = new OcrSettings
            {
                TextLayout = (OcrTextLayout)99,
                Providers =
                [
                    new()
                    {
                        Id = BuiltInProviderIds.BaiduOcr,
                        Kind = (OcrProviderKind)99,
                        DisplayName = "Corrupt Baidu",
                        BaiduOptions = new() { Mode = (BaiduOcrMode)99 },
                    },
                ],
            },
            Selection = new SelectionSettings
            {
                MouseMode = (MouseSelectionMode)99,
                ReadOrder = [(SelectionReadMethod)0, (SelectionReadMethod)9],
            },
            FloatingBall = new FloatingBallSettings
            {
                ClickAction = (IrisAction)999,
            },
            TranslateWindow = new TranslateWindowSettings
            {
                Placement = (WindowPlacementMode)99,
            },
            Network = new NetworkSettings
            {
                ProxyMode = (ProxyMode)99,
            },
        };

        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        // General.Theme
        normalized.General.Theme.ShouldBe(AppTheme.System);
        fixes.ShouldContain(f => f.Contains("General.Theme"));

        // Hotkeys
        normalized.Hotkeys.ContainsKey((IrisAction)999).ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("Hotkeys: 未定义动作 999"));
        normalized.Hotkeys[IrisAction.InputTranslate].Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Shift); // 0x6
        fixes.ShouldContain(f => f.Contains("Modifiers: 0x16 → 0x6"));

        // Translation
        normalized.Translation.AutoCopy.ShouldBe(AutoCopyMode.None);
        normalized.Translation.SelectionLineBreaks.ShouldBe(LineBreakHandling.Keep);
        normalized.Translation.Providers.Any(p => p.Id == "tp-invalid-kind").ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("移除未定义 Kind 99 的 Profile 'tp-invalid-kind'"));
        normalized.Translation.Providers.First(p => p.Id == "tp-valid").OpenAIOptions!.MaxTokensField.ShouldBe(MaxTokensField.MaxTokens);

        // OCR
        normalized.Ocr.TextLayout.ShouldBe(OcrTextLayout.MergeParagraphs);
        var baidu = normalized.Ocr.Providers.First(p => p.Id == BuiltInProviderIds.BaiduOcr);
        baidu.Kind.ShouldBe(OcrProviderKind.BaiduCloud);
        baidu.DisplayName.ShouldBe("百度智能云 OCR");
        baidu.BaiduOptions!.Mode.ShouldBe(BaiduOcrMode.AccurateBasic);

        // Selection
        normalized.Selection.MouseMode.ShouldBe(MouseSelectionMode.Off);
        normalized.Selection.ReadOrder.Count.ShouldBe(1);
        normalized.Selection.ReadOrder[0].ShouldBe(SelectionReadMethod.UIAutomation);
        fixes.ShouldContain(f => f.Contains("Selection.ReadOrder: 移除未定义元素 9"));

        // FloatingBall
        normalized.FloatingBall.ClickAction.ShouldBe(IrisAction.ScreenshotTranslate);

        // TranslateWindow
        normalized.TranslateWindow.Placement.ShouldBe(WindowPlacementMode.FollowCursor);

        // Network
        normalized.Network.ProxyMode.ShouldBe(ProxyMode.System);
    }

    [Fact]
    public void N12_01_GeneralTheme_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { General = new GeneralSettings { Theme = (AppTheme)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.General.Theme.ShouldBe(AppTheme.System);
        fixes.ShouldContain(f => f.Contains("General.Theme"));
    }

    [Fact]
    public void N12_02_HotkeysKey_UndefinedAction_Removed()
    {
        var settings = new AppSettings
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [(IrisAction)999] = new(HotkeyModifiers.Control, "K"),
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Hotkeys.ContainsKey((IrisAction)999).ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("Hotkeys: 未定义动作 999"));
    }

    [Fact]
    public void N12_03_HotkeyModifiers_UndefinedBits_MaskedOut()
    {
        var settings = new AppSettings
        {
            Hotkeys = new Dictionary<IrisAction, HotkeyBinding>
            {
                [IrisAction.InputTranslate] = new((HotkeyModifiers)0x16, "F"),
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Hotkeys[IrisAction.InputTranslate].Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        fixes.ShouldContain(f => f.Contains("Modifiers: 0x16 → 0x6"));
    }

    [Fact]
    public void N12_04_TranslationProviderKind_Undefined_ProfileRemoved()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = "tp-bad-kind",
                        Kind = (TranslationProviderKind)99,
                        DisplayName = "Bad Kind",
                        Model = "gpt-4",
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Translation.Providers.Any(p => p.Id == "tp-bad-kind").ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("移除未定义 Kind 99 的 Profile 'tp-bad-kind'"));
    }

    [Fact]
    public void N12_05_OpenAIMaxTokensField_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "P1",
                        Model = "gpt-4",
                        BaseUrl = "https://api.openai.com/v1",
                        OpenAIOptions = new() { MaxTokensField = (MaxTokensField)99 },
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Translation.Providers.First(p => p.Id == "tp-1").OpenAIOptions!.MaxTokensField.ShouldBe(MaxTokensField.MaxTokens);
        fixes.ShouldContain(f => f.Contains("MaxTokensField"));
    }

    [Fact]
    public void N12_06_TranslationAutoCopy_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { Translation = new TranslationSettings { AutoCopy = (AutoCopyMode)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Translation.AutoCopy.ShouldBe(AutoCopyMode.None);
        fixes.ShouldContain(f => f.Contains("Translation.AutoCopy"));
    }

    [Fact]
    public void N12_07_TranslationSelectionLineBreaks_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { Translation = new TranslationSettings { SelectionLineBreaks = (LineBreakHandling)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Translation.SelectionLineBreaks.ShouldBe(LineBreakHandling.Keep);
        fixes.ShouldContain(f => f.Contains("Translation.SelectionLineBreaks"));
    }

    [Fact]
    public void N12_08_OcrProviderKind_NonBuiltin_Undefined_ProfileRemoved()
    {
        var settings = new AppSettings
        {
            Ocr = new OcrSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = "custom-ocr",
                        Kind = (OcrProviderKind)99,
                        DisplayName = "Custom",
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Ocr.Providers.Any(p => p.Id == "custom-ocr").ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("移除未定义 Kind 99 的 Profile 'custom-ocr'"));
    }

    [Fact]
    public void N12_09_OcrProviderKind_BuiltInBaidu_Undefined_RestoredWithDefaults()
    {
        var settings = new AppSettings
        {
            Ocr = new OcrSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = BuiltInProviderIds.BaiduOcr,
                        Kind = (OcrProviderKind)99,
                        DisplayName = "Corrupt Baidu",
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        var baidu = normalized.Ocr.Providers.First(p => p.Id == BuiltInProviderIds.BaiduOcr);
        baidu.Kind.ShouldBe(OcrProviderKind.BaiduCloud);
        baidu.DisplayName.ShouldBe("百度智能云 OCR");
        fixes.ShouldContain(f => f.Contains("移除未定义 Kind 99 的 Profile 'ocr-baidu'"));
        fixes.ShouldContain(f => f.Contains("ocr-baidu") && f.Contains("追加默认配置"));
    }

    [Fact]
    public void N12_10_BaiduOcrMode_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings
        {
            Ocr = new OcrSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = BuiltInProviderIds.BaiduOcr,
                        Kind = OcrProviderKind.BaiduCloud,
                        DisplayName = "Baidu",
                        BaiduOptions = new() { Mode = (BaiduOcrMode)99 },
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        var baidu = normalized.Ocr.Providers.First(p => p.Id == BuiltInProviderIds.BaiduOcr);
        baidu.BaiduOptions!.Mode.ShouldBe(BaiduOcrMode.AccurateBasic);
        fixes.ShouldContain(f => f.Contains("BaiduOptions.Mode"));
    }

    [Fact]
    public void N12_11_OcrTextLayout_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { Ocr = new OcrSettings { TextLayout = (OcrTextLayout)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Ocr.TextLayout.ShouldBe(OcrTextLayout.MergeParagraphs);
        fixes.ShouldContain(f => f.Contains("Ocr.TextLayout"));
    }

    [Fact]
    public void N12_12_SelectionMouseMode_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { Selection = new SelectionSettings { MouseMode = (MouseSelectionMode)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Selection.MouseMode.ShouldBe(MouseSelectionMode.Off);
        fixes.ShouldContain(f => f.Contains("Selection.MouseMode"));
    }

    [Fact]
    public void N12_13_SelectionReadOrder_UndefinedElements_Removed()
    {
        var settings = new AppSettings
        {
            Selection = new SelectionSettings
            {
                ReadOrder = [(SelectionReadMethod)0, (SelectionReadMethod)9],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Selection.ReadOrder.Count.ShouldBe(1);
        normalized.Selection.ReadOrder[0].ShouldBe(SelectionReadMethod.UIAutomation);
        fixes.ShouldContain(f => f.Contains("Selection.ReadOrder: 移除未定义元素 9"));
    }

    [Fact]
    public void N12_14_FloatingBallClickAction_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { FloatingBall = new FloatingBallSettings { ClickAction = (IrisAction)999 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.FloatingBall.ClickAction.ShouldBe(IrisAction.ScreenshotTranslate);
        fixes.ShouldContain(f => f.Contains("FloatingBall.ClickAction"));
    }

    [Fact]
    public void N12_15_TranslateWindowPlacement_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { TranslateWindow = new TranslateWindowSettings { Placement = (WindowPlacementMode)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.TranslateWindow.Placement.ShouldBe(WindowPlacementMode.FollowCursor);
        fixes.ShouldContain(f => f.Contains("TranslateWindow.Placement"));
    }

    [Fact]
    public void N12_16_NetworkProxyMode_Undefined_NormalizedToDefault()
    {
        var settings = new AppSettings { Network = new NetworkSettings { ProxyMode = (ProxyMode)99 } };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Network.ProxyMode.ShouldBe(ProxyMode.System);
        fixes.ShouldContain(f => f.Contains("Network.ProxyMode"));
    }

    [Fact]
    public void N13_a_EdgeCaseNulls_NormalizedCorrectly()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "TP1",
                        Model = null!,
                        BaseUrl = null,
                        OpenAIOptions = new() { ChatCompletionsPath = null! },
                    },
                ],
                CustomPrompts =
                [
                    new()
                    {
                        Id = "cp-null-sys",
                        Name = "Null Sys",
                        System = null!,
                        User = "User text",
                    },
                ],
            },
            Selection = new SelectionSettings
            {
                ExcludedProcesses = ["notepad.exe", null!, "calc.exe"],
            },
        };

        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        // Model is null -> "" and Enabled = false
        var p1 = normalized.Translation.Providers.First(p => p.Id == "tp-1");
        p1.Model.ShouldBe(string.Empty);
        p1.Enabled.ShouldBeFalse();
        p1.OpenAIOptions!.ChatCompletionsPath.ShouldBe("chat/completions");

        // Custom template with null system removed
        normalized.Translation.CustomPrompts.Any(p => p.Id == "cp-null-sys").ShouldBeFalse();

        // ExcludedProcesses with null removed
        normalized.Selection.ExcludedProcesses.Count.ShouldBe(2);
        normalized.Selection.ExcludedProcesses.ShouldContain("notepad.exe");
        normalized.Selection.ExcludedProcesses.ShouldContain("calc.exe");
        normalized.Selection.ExcludedProcesses.ShouldNotContain(p => p == null!);
    }

    [Fact]
    public void N13_a1_ModelNull_SetToEmptyAndDisabled()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "TP1",
                        Model = null!,
                        Enabled = true,
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        var p = normalized.Translation.Providers.First(p => p.Id == "tp-1");
        p.Model.ShouldBe(string.Empty);
        p.Enabled.ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("Model: null → \"\""));
    }

    [Fact]
    public void N13_a2_BaseUrlNull_ChatCompletionsPathNull_NormalizedToDefaults()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new()
                    {
                        Id = "tp-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "TP1",
                        Model = "gpt-4",
                        BaseUrl = null,
                        OpenAIOptions = new() { ChatCompletionsPath = null! },
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        var p = normalized.Translation.Providers.First(p => p.Id == "tp-1");
        p.OpenAIOptions!.ChatCompletionsPath.ShouldBe("chat/completions");
        fixes.ShouldContain(f => f.Contains("ChatCompletionsPath"));
    }

    [Fact]
    public void N13_a3_CustomPrompt_SystemNull_TemplateRemoved()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                CustomPrompts =
                [
                    new()
                    {
                        Id = "cp-bad",
                        Name = "Bad",
                        System = null!,
                        User = "prompt",
                    },
                ],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Translation.CustomPrompts.Any(p => p.Id == "cp-bad").ShouldBeFalse();
        fixes.ShouldContain(f => f.Contains("System 或 User 为空"));
    }

    [Fact]
    public void N13_a4_ExcludedProcesses_NullItem_Removed()
    {
        var settings = new AppSettings
        {
            Selection = new SelectionSettings
            {
                ExcludedProcesses = ["notepad.exe", null!, "calc.exe"],
            },
        };
        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);
        normalized.Selection.ExcludedProcesses.Count.ShouldBe(2);
        normalized.Selection.ExcludedProcesses.ShouldContain("notepad.exe");
        normalized.Selection.ExcludedProcesses.ShouldContain("calc.exe");
        normalized.Selection.ExcludedProcesses.ShouldNotContain(p => p == null!);
        fixes.ShouldContain(f => f.Contains("ExcludedProcesses: 移除 null 元素"));
    }

    [Fact]
    public void N13_b_Normalize_AllNullFixture_LeavesNoNullInNonNullableMembers()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.all-null.json");
        var jsonText = File.ReadAllText(fixturePath);

        var node = JsonNode.Parse(
            jsonText,
            new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        node.ShouldNotBeNull();

        var fixes = new List<string>();
        SettingsSalvager.Salvage(node, fixes);

        var deserialized = JsonSerializer.Deserialize<AppSettings>(node.ToJsonString(), IrisJson.Options)!;
        var normalized = SettingsNormalizer.Normalize(deserialized, fixes);

        SettingsTestInvariants.AssertInvariants(normalized);
    }

    [Fact]
    public void N13_c_AllNullFixture_CoversEveryReferenceTypeProperty()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.all-null.json");
        var jsonText = File.ReadAllText(fixturePath);

        var reachableRecords = GetReachableRecordTypes(typeof(AppSettings))
            .Where(t => t != typeof(AppSettings))
            .ToList();

        var checkedProps = new HashSet<string>();

        foreach (var type in reachableRecords)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType.IsValueType)
                {
                    continue;
                }

                // Skip container collection properties containing records (which are tested via their elements)
                if (prop.PropertyType.IsGenericType &&
                    (prop.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                     prop.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
                {
                    var elemType = prop.PropertyType.GetGenericArguments().Last();
                    if (IsRecord(elemType))
                    {
                        continue;
                    }
                }

                var camelCase = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                if (checkedProps.Add(camelCase))
                {
                    var pattern = $"\"{camelCase}\"\\s*:\\s*null";
                    Regex.IsMatch(jsonText, pattern).ShouldBeTrue(
                        $"Property '{type.Name}.{prop.Name}' (camelCase '{camelCase}') must appear with null value in settings.all-null.json");
                }
            }
        }

        checkedProps.Count.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void N14_ReferenceIdRewriting_ExactMatch_And_OcrProfilesCopied()
    {
        var settings = new AppSettings
        {
            Ocr = new OcrSettings
            {
                ActiveProviderId = "OCR-BAIDU",
                FallbackProviderId = "OCR-WINDOWS",
                Providers = [],
            },
            Translation = new TranslationSettings
            {
                DefaultPromptTemplateId = "BUILTIN.TRANSLATE.V1",
            },
        };

        var fixes = new List<string>();
        var normalized = SettingsNormalizer.Normalize(settings, fixes);

        // ID case rewriting to exact match
        normalized.Ocr.ActiveProviderId.ShouldBe(BuiltInProviderIds.BaiduOcr);
        normalized.Ocr.FallbackProviderId.ShouldBe(BuiltInProviderIds.WindowsOcr);
        normalized.Translation.DefaultPromptTemplateId.ShouldBe(BuiltInPrompts.DefaultTemplateId);

        // Restored built-in profiles must equal new OcrSettings().Providers
        var defaultProviders = new OcrSettings().Providers;
        normalized.Ocr.Providers.Count.ShouldBe(2);
        normalized.Ocr.Providers[0].ShouldBe(defaultProviders[0]);
        normalized.Ocr.Providers[1].ShouldBe(defaultProviders[1]);
    }

    private static HashSet<Type> GetReachableRecordTypes(Type root)
    {
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!visited.Add(type))
            {
                continue;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propType = prop.PropertyType;
                if (propType.IsGenericType)
                {
                    foreach (var arg in propType.GetGenericArguments())
                    {
                        if (IsRecord(arg))
                        {
                            queue.Enqueue(arg);
                        }
                    }
                }
                else if (IsRecord(propType))
                {
                    queue.Enqueue(propType);
                }
            }
        }

        return visited;
    }

    private static bool IsRecord(Type type)
    {
        return type.IsClass &&
            (type.GetMethod("<clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null ||
             type.GetProperty("EqualityContract", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null);
    }
}
