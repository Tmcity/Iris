using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Contracts;

public class AppSettingsSerializationTests
{
    [Fact]
    public void Default_RoundTrip_IsStable()
    {
        var original = new AppSettings();
        var json1 = JsonSerializer.Serialize(original, IrisJson.Options);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json1, IrisJson.Options);
        deserialized.ShouldNotBeNull();
        var json2 = JsonSerializer.Serialize(deserialized, IrisJson.Options);
        json2.ShouldBe(json1);
    }

    [Fact]
    public void EmptyJson_YieldsDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", IrisJson.Options);
        settings.ShouldNotBeNull();
        settings.Hotkeys.Count.ShouldBe(7);
        settings.Ocr.Providers.Select(p => p.Id).ShouldBe(["ocr-baidu", "ocr-windows"]);
        settings.Translation.PrimaryTargetLanguage.ShouldBe("zh-Hans");
    }

    [Fact]
    public void SpecExample_Deserializes()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.example.json");
        File.Exists(fixturePath).ShouldBeTrue($"Fixture not found at {fixturePath}");

        var raw = File.ReadAllText(fixturePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(raw, IrisJson.Options);
        settings.ShouldNotBeNull();
        settings.Translation.Providers.Count.ShouldBe(2);
        settings.Translation.Providers[1].Kind.ShouldBe(TranslationProviderKind.Anthropic);
        settings.Translation.Providers[1].Model.ShouldBe("claude-opus-5");

        var serialized = JsonSerializer.Serialize(settings, IrisJson.Options);
        Normalize(serialized).ShouldBe(Normalize(raw));

        static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
    }

    [Fact]
    public void Enums_SerializeAsStrings()
    {
        var settings = new AppSettings();
        var json = JsonSerializer.Serialize(settings, IrisJson.Options);

        json.ShouldContain("\"theme\": \"System\"");
        json.ShouldContain("\"modifiers\": \"Alt, Control\"");
    }
}
