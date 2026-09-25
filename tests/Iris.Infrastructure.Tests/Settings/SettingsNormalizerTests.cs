using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Iris.Core.Actions;
using Iris.Core.Languages;
using Iris.Core.Ocr;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Infrastructure.Settings;
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
}
