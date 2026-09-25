using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Infrastructure.Settings;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.Settings;

public sealed class SettingsSalvagerTests
{
    private static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(
            json,
            new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!
            .AsObject();
    }

    [Fact]
    public void Salvage_WhenOneProviderMissingModel_RemovesOnlyThatElement()
    {
        var json = """
        {
          "translation": {
            "providers": [
              {
                "id": "p1",
                "kind": "OpenAICompatible",
                "displayName": "P1",
                "model": "gpt-4o"
              },
              {
                "id": "p2",
                "kind": "OpenAICompatible",
                "displayName": "P2"
              },
              {
                "id": "p3",
                "kind": "OpenAICompatible",
                "displayName": "P3",
                "model": "gpt-4o-mini"
              }
            ]
          }
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.Count.ShouldBe(1);
        fixes[0].ShouldStartWith("translation.providers[1]:");

        var providers = root["translation"]?["providers"]?.AsArray();
        providers.ShouldNotBeNull();
        providers.Count.ShouldBe(2);
        providers[0]?["id"]?.GetValue<string>().ShouldBe("p1");
        providers[1]?["id"]?.GetValue<string>().ShouldBe("p3");
    }

    [Fact]
    public void Salvage_WhenTranslationProvidersNotArray_PropertyRemoved()
    {
        var json = """
        {
          "translation": {
            "providers": "invalid_string_not_array"
          }
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.Count.ShouldBe(1);
        fixes[0].ShouldStartWith("translation.providers:");

        var trans = root["translation"]?.AsObject();
        trans.ShouldNotBeNull();
        trans.ContainsKey("providers").ShouldBeFalse();
    }

    [Fact]
    public void Salvage_CustomPromptsInvalidElement_AndOcrProvidersInvalidKind_AreRemoved()
    {
        var json = """
        {
          "translation": {
            "customPrompts": [
              { "id": "valid1", "name": "Valid", "system": "System prompt", "user": "User prompt" },
              { "name": "MissingIdAndTemplate" }
            ]
          },
          "ocr": {
            "providers": [
              { "id": "ocr1", "kind": "BaiduCloud", "displayName": "Baidu" },
              { "id": "ocr2", "kind": "UnknownKind123", "displayName": "Unknown" }
            ]
          }
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.Count.ShouldBe(2);
        fixes.ShouldContain(f => f.StartsWith("translation.customPrompts[1]:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("ocr.providers[1]:", StringComparison.Ordinal));

        var prompts = root["translation"]?["customPrompts"]?.AsArray();
        prompts.ShouldNotBeNull();
        prompts.Count.ShouldBe(1);
        prompts[0]?["id"]?.GetValue<string>().ShouldBe("valid1");

        var ocrProviders = root["ocr"]?["providers"]?.AsArray();
        ocrProviders.ShouldNotBeNull();
        ocrProviders.Count.ShouldBe(1);
        ocrProviders[0]?["id"]?.GetValue<string>().ShouldBe("ocr1");
    }

    [Fact]
    public void Salvage_Hotkeys_HandlesCasesCorrectly()
    {
        // 1. Lowercase name rewritten to canonical
        // 2. Key "3" -> ScreenshotTranslate
        // 3. Invalid key removed
        // 4. Invalid modifiers removed
        // 5. null value preserved
        // 6. Duplicate after rewrite dropped
        var json = """
        {
          "hotkeys": {
            "inputtranslate": { "modifiers": "Control, Alt", "key": "F", "enabled": true },
            "3": { "modifiers": "Control, Alt", "key": "S", "enabled": true },
            "ScreenshotTranslate": { "modifiers": "Control, Alt", "key": "S2", "enabled": true },
            "invalid_action_name": { "modifiers": "Control, Alt", "key": "X", "enabled": true },
            "ScreenshotOcr": { "modifiers": "InvalidModName999", "key": "O", "enabled": true },
            "ClipboardTranslate": null
          }
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        // inputtranslate: rewritten -> hotkeys.inputtranslate: 键名已规范为 InputTranslate
        // 3: rewritten -> hotkeys.3: 键名已规范为 ScreenshotTranslate
        // ScreenshotTranslate: duplicate after 3 -> hotkeys.ScreenshotTranslate: 已移除（键名重复）
        // invalid_action_name: removed -> hotkeys.invalid_action_name: 已移除
        // ScreenshotOcr: bad modifier -> hotkeys.ScreenshotOcr: 已移除
        // ClipboardTranslate: null preserved -> no fix for this
        fixes.ShouldContain(f => f.StartsWith("hotkeys.inputtranslate:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("hotkeys.3:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("hotkeys.ScreenshotTranslate:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("hotkeys.invalid_action_name:", StringComparison.Ordinal));
        fixes.ShouldContain(f => f.StartsWith("hotkeys.ScreenshotOcr:", StringComparison.Ordinal));

        var hotkeys = root["hotkeys"]?.AsObject();
        hotkeys.ShouldNotBeNull();
        hotkeys.ContainsKey("InputTranslate").ShouldBeTrue();
        hotkeys.ContainsKey("ScreenshotTranslate").ShouldBeTrue();
        hotkeys.ContainsKey("ClipboardTranslate").ShouldBeTrue();
        hotkeys["ClipboardTranslate"].ShouldBeNull();
        hotkeys.ContainsKey("ScreenshotOcr").ShouldBeFalse();
        hotkeys.ContainsKey("invalid_action_name").ShouldBeFalse();
    }

    [Fact]
    public void Salvage_Hotkeys_WhenNotObject_IsRemoved()
    {
        var json = """
        {
          "hotkeys": "not_an_object"
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.Count.ShouldBe(1);
        fixes[0].ShouldStartWith("hotkeys:");
        root.ContainsKey("hotkeys").ShouldBeFalse();
    }

    [Fact]
    public void Salvage_GeneralThemePurple_RemovesOnlyGeneralSection()
    {
        var json = """
        {
          "general": {
            "theme": "Purple"
          },
          "history": {
            "maxEntries": 500
          }
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.Count.ShouldBe(1);
        fixes[0].ShouldStartWith("general:");
        root.ContainsKey("general").ShouldBeFalse();
        root.ContainsKey("history").ShouldBeTrue();
        root["history"]?["maxEntries"]?.GetValue<int>().ShouldBe(500);
    }

    [Fact]
    public void Salvage_SectionCaseInsensitive_IdentifiedAndHandled()
    {
        var json = """
        {
          "General": {
            "Theme": "InvalidTheme"
          }
        }
        """;

        var root = ParseObject(json);
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.Count.ShouldBe(1);
        fixes[0].ShouldStartWith("general:");
        root.ContainsKey("General").ShouldBeFalse();
        root.ContainsKey("general").ShouldBeFalse();
    }

    [Fact]
    public void Salvage_SpecExample_HasZeroFixesAndRemainsUnchanged()
    {
        var examplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.example.json");
        var json = File.ReadAllText(examplePath);
        var root = ParseObject(json);
        var initialJson = root.ToJsonString();
        var fixes = new List<string>();

        SettingsSalvager.Salvage(root, fixes);

        fixes.ShouldBeEmpty();
        root.ToJsonString().ShouldBe(initialJson);
    }
}
