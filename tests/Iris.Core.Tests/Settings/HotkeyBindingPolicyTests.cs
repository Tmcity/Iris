using System;
using System.IO;
using System.Text.Json;
using Iris.Core.Settings;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Settings;

public sealed class HotkeyBindingPolicyTests
{
    private readonly HotkeyBindingPolicy _policy = new();

    private static string ResolveFixturePath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Fixtures", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\Fixtures", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\Fixtures", fileName)),
            Path.Combine(Path.GetTempPath(), "iris-t017-review-prep", fileName),
            Path.Combine(Path.GetTempPath(), "iris-t020a-review-prep", fileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Required test fixture '{fileName}' not found in candidates:\n{string.Join(Environment.NewLine, candidates)}");
    }

    [Fact]
    public void NullBinding_ReturnsFalseAndNull()
    {
        _policy.TryNormalize(null, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void EnabledTrue_BlankKey_ReturnsFalse(string blankKey)
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Control, blankKey, Enabled: true);
        _policy.TryNormalize(binding, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void EnabledFalse_BlankKey_NormalizesToCanonicalDisabledEmpty(string blankKey)
    {
        // 任何空白 key 在 Enabled=false 时规范化为 (None, "", false)
        var bindingNone = new HotkeyBinding(HotkeyModifiers.None, blankKey, Enabled: false);
        _policy.TryNormalize(bindingNone, out var normNone).ShouldBeTrue();
        normNone.ShouldNotBeNull();
        normNone.ShouldBe(new HotkeyBinding(HotkeyModifiers.None, string.Empty, Enabled: false));

        var bindingAlt = new HotkeyBinding(HotkeyModifiers.Alt, blankKey, Enabled: false);
        _policy.TryNormalize(bindingAlt, out var normAlt).ShouldBeTrue();
        normAlt.ShouldNotBeNull();
        normAlt.ShouldBe(new HotkeyBinding(HotkeyModifiers.None, string.Empty, Enabled: false));
    }

    [Fact]
    public void EnabledFalse_BlankKey_InvalidModifiers_ReturnsFalse()
    {
        // 包含未知位或负数修饰符，即使禁用空键也必须拒绝新编辑输入
        var invalidMask = new HotkeyBinding((HotkeyModifiers)0x10, "", Enabled: false);
        _policy.TryNormalize(invalidMask, out var normMask).ShouldBeFalse();
        normMask.ShouldBeNull();

        var negativeMask = new HotkeyBinding((HotkeyModifiers)(-1), "", Enabled: false);
        _policy.TryNormalize(negativeMask, out var normNeg).ShouldBeFalse();
        normNeg.ShouldBeNull();
    }

    [Theory]
    [InlineData("F12")]
    [InlineData("f12")]
    public void EnabledFalse_F12OrShiftA_CanBePreservedForEditing(string f12Key)
    {
        // 禁用状态下允许保留 F12 或 Shift+A 供编辑
        var f12Binding = new HotkeyBinding(HotkeyModifiers.Control, f12Key, Enabled: false);
        _policy.TryNormalize(f12Binding, out var normF12).ShouldBeTrue();
        normF12.ShouldNotBeNull();
        normF12.Key.ShouldBe("F12");
        normF12.Modifiers.ShouldBe(HotkeyModifiers.Control);
        normF12.Enabled.ShouldBeFalse();

        var shiftABinding = new HotkeyBinding(HotkeyModifiers.Shift, "a", Enabled: false);
        _policy.TryNormalize(shiftABinding, out var normShiftA).ShouldBeTrue();
        normShiftA.ShouldNotBeNull();
        normShiftA.Key.ShouldBe("A");
        normShiftA.Modifiers.ShouldBe(HotkeyModifiers.Shift);
        normShiftA.Enabled.ShouldBeFalse();

        var noneABinding = new HotkeyBinding(HotkeyModifiers.None, "a", Enabled: false);
        _policy.TryNormalize(noneABinding, out var normNoneA).ShouldBeTrue();
        normNoneA.ShouldNotBeNull();
        normNoneA.Key.ShouldBe("A");
        normNoneA.Modifiers.ShouldBe(HotkeyModifiers.None);
        normNoneA.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void EnabledTrue_F12_AlwaysRejected()
    {
        foreach (var mod in Enum.GetValues<HotkeyModifiers>())
        {
            var binding = new HotkeyBinding(mod, "F12", Enabled: true);
            _policy.TryNormalize(binding, out var normalized).ShouldBeFalse();
            normalized.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("Ctrl+A")]
    [InlineData("65")]
    [InlineData("0x41")]
    [InlineData("Return")]
    [InlineData("PrintScreen")]
    [InlineData("Oem8")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("NumPad10")]
    public void InvalidKeyStrings_Rejected(string invalidKey)
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Control, invalidKey, Enabled: true);
        _policy.TryNormalize(binding, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();

        var disabledBinding = new HotkeyBinding(HotkeyModifiers.Control, invalidKey, Enabled: false);
        _policy.TryNormalize(disabledBinding, out var normDisabled).ShouldBeFalse();
        normDisabled.ShouldBeNull();
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(16384)]
    [InlineData(-1)]
    public void InvalidModifierFlags_Rejected(int invalidMask)
    {
        var binding = new HotkeyBinding((HotkeyModifiers)invalidMask, "A", Enabled: true);
        _policy.TryNormalize(binding, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();

        var disabledBinding = new HotkeyBinding((HotkeyModifiers)invalidMask, "A", Enabled: false);
        _policy.TryNormalize(disabledBinding, out var normDisabled).ShouldBeFalse();
        normDisabled.ShouldBeNull();
    }

    [Fact]
    public void StandaloneKeys_SnapshotAndF13ToF24_AllowedWithoutModifiers()
    {
        var snapshot = new HotkeyBinding(HotkeyModifiers.None, "Snapshot", Enabled: true);
        _policy.TryNormalize(snapshot, out var normSnapshot).ShouldBeTrue();
        normSnapshot.ShouldNotBeNull();
        normSnapshot.Key.ShouldBe("Snapshot");
        normSnapshot.Modifiers.ShouldBe(HotkeyModifiers.None);

        for (int i = 13; i <= 24; i++)
        {
            var fKey = new HotkeyBinding(HotkeyModifiers.None, $"f{i}", Enabled: true);
            _policy.TryNormalize(fKey, out var normF).ShouldBeTrue();
            normF.ShouldNotBeNull();
            normF.Key.ShouldBe($"F{i}");
            normF.Modifiers.ShouldBe(HotkeyModifiers.None);
        }

        // F1–F12 standalone are rejected
        for (int i = 1; i <= 12; i++)
        {
            var fKey = new HotkeyBinding(HotkeyModifiers.None, $"F{i}", Enabled: true);
            _policy.TryNormalize(fKey, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void ShiftPlusLetter_RejectedWhenEnabled()
    {
        for (int i = 0; i < 26; i++)
        {
            string letter = ((char)('A' + i)).ToString();
            var binding = new HotkeyBinding(HotkeyModifiers.Shift, letter, Enabled: true);
            _policy.TryNormalize(binding, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void CasingAndWhitespace_NormalizedToCanonical()
    {
        var binding1 = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "  d  ", Enabled: true);
        _policy.TryNormalize(binding1, out var norm1).ShouldBeTrue();
        norm1.ShouldNotBeNull();
        norm1.Key.ShouldBe("D");
        norm1.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);

        var binding2 = new HotkeyBinding(HotkeyModifiers.Control, "  oemtilde  ", Enabled: true);
        _policy.TryNormalize(binding2, out var norm2).ShouldBeTrue();
        norm2.ShouldNotBeNull();
        norm2.Key.ShouldBe("OemTilde");

        var binding3 = new HotkeyBinding(HotkeyModifiers.Control, "  numpad5  ", Enabled: true);
        _policy.TryNormalize(binding3, out var norm3).ShouldBeTrue();
        norm3.ShouldNotBeNull();
        norm3.Key.ShouldBe("NumPad5");
    }

    [Fact]
    public void ExpectedVectors_1536Matrix_MatchesExpectation()
    {
        string path = ResolveFixturePath("hotkey-expected-vectors.json");
        File.Exists(path).ShouldBeTrue($"Required test fixture '{path}' must exist.");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 1. 验证 keyMap (96 键)
        var keyMapArray = root.GetProperty("keyMap");
        keyMapArray.GetArrayLength().ShouldBe(96);
        foreach (var item in keyMapArray.EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            string variant = item.GetProperty("normalizationVariant").GetString()!;

            // 禁用状态下所有 96 键（包括 F12）均能成功规范化
            var disabledBinding = new HotkeyBinding(HotkeyModifiers.Control, variant, Enabled: false);
            _policy.TryNormalize(disabledBinding, out var normDisabled).ShouldBeTrue();
            normDisabled.ShouldNotBeNull();
            normDisabled.Key.ShouldBe(key);

            // 启用状态下除了 F12（系统调试器保留）之外，其余 95 键均成功规范化
            var enabledBinding = new HotkeyBinding(HotkeyModifiers.Control, variant, Enabled: true);
            if (string.Equals(key, "F12", StringComparison.Ordinal))
            {
                _policy.TryNormalize(enabledBinding, out _).ShouldBeFalse();
            }
            else
            {
                _policy.TryNormalize(enabledBinding, out var normEnabled).ShouldBeTrue();
                normEnabled.ShouldNotBeNull();
                normEnabled.Key.ShouldBe(key);
            }
        }

        // 2. 验证 enabledValidation (1536 组用例)
        var validationArray = root.GetProperty("enabledValidation");
        validationArray.GetArrayLength().ShouldBe(1536);
        int validCount = 0;
        int invalidCount = 0;

        foreach (var item in validationArray.EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            int modifiers = item.GetProperty("modifiers").GetInt32();
            bool expectedPasses = item.GetProperty("passesInputValidation").GetBoolean();

            var binding = new HotkeyBinding((HotkeyModifiers)modifiers, key, Enabled: true);
            bool actualPasses = _policy.TryNormalize(binding, out var norm);

            actualPasses.ShouldBe(expectedPasses, $"Failed for Key={key}, Modifiers={modifiers}");
            if (actualPasses)
            {
                norm.ShouldNotBeNull();
                norm.Key.ShouldBe(key);
                norm.Modifiers.ShouldBe((HotkeyModifiers)modifiers);
                norm.Enabled.ShouldBeTrue();
                validCount++;
            }
            else
            {
                norm.ShouldBeNull();
                invalidCount++;
            }
        }

        validCount.ShouldBe(1412);
        invalidCount.ShouldBe(124);
    }

    [Fact]
    public void ExpectedVectors_3158IndependentEditVectors_MatchesExpectation()
    {
        string path = ResolveFixturePath("hotkey-edit-expected-vectors.json");
        File.Exists(path).ShouldBeTrue($"Required test fixture '{path}' must exist.");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cases = root.GetProperty("cases");
        cases.GetArrayLength().ShouldBe(3158);

        int validCount = 0;
        int invalidCount = 0;

        foreach (var item in cases.EnumerateArray())
        {
            string? key = item.GetProperty("key").ValueKind == JsonValueKind.Null ? null : item.GetProperty("key").GetString();
            int modifiers = item.GetProperty("modifiers").GetInt32();
            bool enabled = item.GetProperty("enabled").GetBoolean();
            bool expectedValid = item.GetProperty("valid").GetBoolean();

            var binding = new HotkeyBinding((HotkeyModifiers)modifiers, key!, enabled);

            bool actualValid = _policy.TryNormalize(binding, out var norm);
            actualValid.ShouldBe(expectedValid, $"Mismatch for key='{key}', mod={modifiers}, enabled={enabled}");

            if (expectedValid)
            {
                validCount++;
                norm.ShouldNotBeNull();
                var expectedNorm = item.GetProperty("normalized");
                string expectedKey = expectedNorm.GetProperty("key").GetString()!;
                int expectedMod = expectedNorm.GetProperty("modifiers").GetInt32();
                bool expectedEnabled = expectedNorm.GetProperty("enabled").GetBoolean();

                norm.Key.ShouldBe(expectedKey);
                norm.Modifiers.ShouldBe((HotkeyModifiers)expectedMod);
                norm.Enabled.ShouldBe(expectedEnabled);
            }
            else
            {
                invalidCount++;
                norm.ShouldBeNull();
            }
        }

        validCount.ShouldBe(2972);
        invalidCount.ShouldBe(186);
    }
}
