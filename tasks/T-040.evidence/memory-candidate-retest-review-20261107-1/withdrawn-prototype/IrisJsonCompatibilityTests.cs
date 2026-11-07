using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Core.Actions;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.Core.Tests.Contracts;

public sealed class IrisJsonCompatibilityTests
{
    [Theory]
    [InlineData("defaults")]
    [InlineData("example")]
    [InlineData("web_options")]
    [InlineData("nullable_values")]
    public void SettingsGeneratedAccessors_PreserveExistingJsonContract(string scenario)
    {
        string raw = scenario switch
        {
            "defaults" => "{}",
            "example" => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.example.json")),
            "web_options" => """
                { // existing Web options allow case differences, numbers as strings and trailing commas
                  "SCHEMAVERSION": "1",
                  "GENERAL": { "Theme": "Dark", "LaunchAtStartup": true, },
                  "translation": { "maxInputChars": "4500", "maxConcurrentProviders": "2" },
                  "unknownFutureSection": { "value": "Iris synthetic compatibility fixture" },
                }
                """,
            "nullable_values" => """
                { "general": null,
                  "floatingBall": { "position": { "x": -100, "y": 25 }, "monitorDeviceName": "synthetic-monitor" },
                  "translateWindow": { "lastBounds": { "x": 10, "y": 20, "width": 460, "height": 640 } },
                  "network": { "proxyMode": "Custom", "customProxyUrl": "http://127.0.0.1:18041/", "proxyCredentialSetId": null } }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var reflection = ReflectionOptions();
        var oldValue = JsonSerializer.Deserialize<AppSettings>(raw, reflection);
        var generatedValue = JsonSerializer.Deserialize<AppSettings>(raw, IrisJson.Options);
        oldValue.ShouldNotBeNull();
        generatedValue.ShouldNotBeNull();
        JsonSerializer.Serialize(generatedValue, IrisJson.Options)
            .ShouldBe(JsonSerializer.Serialize(oldValue, reflection));
    }

    [Fact]
    public void CompactIpcCopy_PreservesPayloadAndEnumBytes()
    {
        var original = new IpcCommand(IrisAction.InputTranslate, "Iris synthetic payload 中文 <tag>\nsecond line");
        var generated = new JsonSerializerOptions(IrisJson.Options) { WriteIndented = false };
        var reflection = ReflectionOptions();
        reflection.WriteIndented = false;
        byte[] oldBytes = JsonSerializer.SerializeToUtf8Bytes(original, reflection);
        JsonSerializer.SerializeToUtf8Bytes(original, generated).ShouldBe(oldBytes);
        JsonSerializer.Deserialize<IpcCommand>(oldBytes, generated).ShouldBe(original);
    }

    [Fact]
    public void MutableCreateOptions_PreservesCallerFormattingOverrides()
    {
        var generated = IrisJson.Create();
        var reflection = ReflectionOptions();
        foreach (var options in new[] { generated, reflection })
        {
            options.WriteIndented = false;
            options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        }
        var settings = new AppSettings();
        JsonSerializer.SerializeToUtf8Bytes(settings, generated)
            .ShouldBe(JsonSerializer.SerializeToUtf8Bytes(settings, reflection));
    }

    [Fact]
    public void UnregisteredCallerDto_StillUsesCompatibleReflectionFallback()
    {
        var original = new CallerDto("Iris synthetic caller", 12.5m);
        byte[] baseline = JsonSerializer.SerializeToUtf8Bytes(original, ReflectionOptions());
        JsonSerializer.SerializeToUtf8Bytes(original, IrisJson.Options).ShouldBe(baseline);
        JsonSerializer.Deserialize<CallerDto>(baseline, IrisJson.Options).ShouldBe(original);
    }

    private static JsonSerializerOptions ReflectionOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public sealed record CallerDto(string Name, decimal Value);
}
