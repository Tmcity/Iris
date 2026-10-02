using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Iris.Core.Settings;
using Iris.Platform.Windows.Hotkey;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Hotkey;

public sealed class KeyNameMapperTests
{
    private const string ExpectedVectorsPath = @"C:\Users\28910\AppData\Local\Temp\iris-t017-review-prep\hotkey-expected-vectors.json";

    [Fact]
    public void CanonicalKeys_All96WhitelistKeys_MapToExactVk()
    {
        // 1. Letters A–Z (0x41–0x5A) -> 26 keys
        for (int i = 0; i < 26; i++)
        {
            string key = ((char)('A' + i)).ToString();
            KeyNameMapper.TryMapToVk(key, out uint vk).ShouldBeTrue();
            vk.ShouldBe((uint)(0x41 + i));
        }

        // 2. D0–D9 (0x30–0x39) -> 10 keys
        for (int i = 0; i <= 9; i++)
        {
            KeyNameMapper.TryMapToVk($"D{i}", out uint vk).ShouldBeTrue();
            vk.ShouldBe((uint)(0x30 + i));
        }

        // 3. F1–F24 (0x70–0x87) -> 24 keys
        for (int i = 1; i <= 24; i++)
        {
            KeyNameMapper.TryMapToVk($"F{i}", out uint vk).ShouldBeTrue();
            vk.ShouldBe((uint)(0x70 + (i - 1)));
        }

        // 4. NumPad0–NumPad9 (0x60–0x69) -> 10 keys
        for (int i = 0; i <= 9; i++)
        {
            KeyNameMapper.TryMapToVk($"NumPad{i}", out uint vk).ShouldBeTrue();
            vk.ShouldBe((uint)(0x60 + i));
        }

        // 5. Navigation & editing -> 15 keys
        KeyNameMapper.TryMapToVk("Space", out uint spaceVk).ShouldBeTrue(); spaceVk.ShouldBe(0x20u);
        KeyNameMapper.TryMapToVk("Enter", out uint enterVk).ShouldBeTrue(); enterVk.ShouldBe(0x0Du);
        KeyNameMapper.TryMapToVk("Tab", out uint tabVk).ShouldBeTrue(); tabVk.ShouldBe(0x09u);
        KeyNameMapper.TryMapToVk("Escape", out uint escVk).ShouldBeTrue(); escVk.ShouldBe(0x1Bu);
        KeyNameMapper.TryMapToVk("Insert", out uint insVk).ShouldBeTrue(); insVk.ShouldBe(0x2Du);
        KeyNameMapper.TryMapToVk("Delete", out uint delVk).ShouldBeTrue(); delVk.ShouldBe(0x2Eu);
        KeyNameMapper.TryMapToVk("Home", out uint homeVk).ShouldBeTrue(); homeVk.ShouldBe(0x24u);
        KeyNameMapper.TryMapToVk("End", out uint endVk).ShouldBeTrue(); endVk.ShouldBe(0x23u);
        KeyNameMapper.TryMapToVk("PageUp", out uint pgUpVk).ShouldBeTrue(); pgUpVk.ShouldBe(0x21u);
        KeyNameMapper.TryMapToVk("PageDown", out uint pgDnVk).ShouldBeTrue(); pgDnVk.ShouldBe(0x22u);
        KeyNameMapper.TryMapToVk("Left", out uint leftVk).ShouldBeTrue(); leftVk.ShouldBe(0x25u);
        KeyNameMapper.TryMapToVk("Up", out uint upVk).ShouldBeTrue(); upVk.ShouldBe(0x26u);
        KeyNameMapper.TryMapToVk("Right", out uint rightVk).ShouldBeTrue(); rightVk.ShouldBe(0x27u);
        KeyNameMapper.TryMapToVk("Down", out uint downVk).ShouldBeTrue(); downVk.ShouldBe(0x28u);
        KeyNameMapper.TryMapToVk("Snapshot", out uint snapVk).ShouldBeTrue(); snapVk.ShouldBe(0x2Cu);

        // 6. OEM keys -> 11 keys
        KeyNameMapper.TryMapToVk("OemTilde", out uint oemTilde).ShouldBeTrue(); oemTilde.ShouldBe(0xC0u);
        KeyNameMapper.TryMapToVk("OemMinus", out uint oemMinus).ShouldBeTrue(); oemMinus.ShouldBe(0xBDu);
        KeyNameMapper.TryMapToVk("OemPlus", out uint oemPlus).ShouldBeTrue(); oemPlus.ShouldBe(0xBBu);
        KeyNameMapper.TryMapToVk("OemOpenBrackets", out uint oemOpen).ShouldBeTrue(); oemOpen.ShouldBe(0xDBu);
        KeyNameMapper.TryMapToVk("OemCloseBrackets", out uint oemClose).ShouldBeTrue(); oemClose.ShouldBe(0xDDu);
        KeyNameMapper.TryMapToVk("OemPipe", out uint oemPipe).ShouldBeTrue(); oemPipe.ShouldBe(0xDCu);
        KeyNameMapper.TryMapToVk("OemSemicolon", out uint oemSemi).ShouldBeTrue(); oemSemi.ShouldBe(0xBAu);
        KeyNameMapper.TryMapToVk("OemQuotes", out uint oemQuotes).ShouldBeTrue(); oemQuotes.ShouldBe(0xDEu);
        KeyNameMapper.TryMapToVk("OemComma", out uint oemComma).ShouldBeTrue(); oemComma.ShouldBe(0xBCu);
        KeyNameMapper.TryMapToVk("OemPeriod", out uint oemPeriod).ShouldBeTrue(); oemPeriod.ShouldBe(0xBEu);
        KeyNameMapper.TryMapToVk("OemQuestion", out uint oemQuest).ShouldBeTrue(); oemQuest.ShouldBe(0xBFu);
    }

    [Theory]
    [InlineData(" a ", 0x41u)]
    [InlineData("  f12  ", 0x7Bu)]
    [InlineData("oemtilde", 0xC0u)]
    [InlineData("OEMTILDE", 0xC0u)]
    [InlineData("SNAPSHOT", 0x2Cu)]
    [InlineData("  space  ", 0x20u)]
    [InlineData("numpad5", 0x65u)]
    public void TryMapToVk_WhitespaceAndCaseInsensitive_MapsSuccessfully(string input, uint expectedVk)
    {
        KeyNameMapper.TryMapToVk(input, out uint vk).ShouldBeTrue();
        vk.ShouldBe(expectedVk);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("65")]
    [InlineData("0x41")]
    [InlineData("Ctrl+A")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("NumPad10")]
    [InlineData("Return")]
    [InlineData("PrintScreen")]
    [InlineData("Oem8")]
    [InlineData("Esc")]
    [InlineData(null)]
    public void TryMapToVk_RejectedKeyStrings_ReturnsFalse(string? input)
    {
        KeyNameMapper.TryMapToVk(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void F12_CanMapToVk_ButRejectedInValidateBinding()
    {
        KeyNameMapper.TryMapToVk("F12", out uint vk).ShouldBeTrue();
        vk.ShouldBe(0x7Bu);

        // F12 with any modifiers must fail validation
        for (int m = 0; m <= 15; m++)
        {
            var binding = new HotkeyBinding((HotkeyModifiers)m, "F12");
            KeyNameMapper.ValidateBinding(binding, out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(16384)]
    [InlineData(-1)]
    public void ValidateBinding_RejectedModifierMasks_ReturnsFalse(int invalidMask)
    {
        var binding = new HotkeyBinding((HotkeyModifiers)invalidMask, "A");
        KeyNameMapper.ValidateBinding(binding, out _).ShouldBeFalse();
    }

    [Fact]
    public void ValidateBinding_StandaloneRules_SnapshotAndF13ToF24Allowed_OthersDisallowed()
    {
        // Snapshot standalone -> valid
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.None, "Snapshot"), out uint snapVk).ShouldBeTrue();
        snapVk.ShouldBe(0x2Cu);

        // F13–F24 standalone -> valid
        for (int i = 13; i <= 24; i++)
        {
            KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.None, $"F{i}"), out uint fVk).ShouldBeTrue();
            fVk.ShouldBe((uint)(0x70 + (i - 1)));
        }

        // F1–F11 standalone -> invalid
        for (int i = 1; i <= 11; i++)
        {
            KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.None, $"F{i}"), out _).ShouldBeFalse();
        }

        // Letter standalone -> invalid
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.None, "A"), out _).ShouldBeFalse();

        // Digit standalone -> invalid
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.None, "D1"), out _).ShouldBeFalse();
    }

    [Fact]
    public void ValidateBinding_ShiftPlusLetter_Disallowed_ShiftPlusNonLetter_Allowed()
    {
        // Only Shift + Letter A–Z -> Disallowed
        for (int i = 0; i < 26; i++)
        {
            string letter = ((char)('A' + i)).ToString();
            KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Shift, letter), out _).ShouldBeFalse();
        }

        // Ctrl + Shift + Letter -> Allowed
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "A"), out _).ShouldBeTrue();

        // Alt + Shift + Letter -> Allowed
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "A"), out _).ShouldBeTrue();

        // Shift + Non-letter -> Allowed
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Shift, "F1"), out _).ShouldBeTrue();
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Shift, "Snapshot"), out _).ShouldBeTrue();
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Shift, "Enter"), out _).ShouldBeTrue();
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Shift, "D1"), out _).ShouldBeTrue();
        KeyNameMapper.ValidateBinding(new HotkeyBinding(HotkeyModifiers.Shift, "OemTilde"), out _).ShouldBeTrue();
    }

    [Fact]
    public void CrossVerify_WithExpectedVectorsJson_IfPresent()
    {
        if (!File.Exists(ExpectedVectorsPath))
        {
            return;
        }

        string json = File.ReadAllText(ExpectedVectorsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 1. Verify keyMap (96 keys)
        var keyMapArray = root.GetProperty("keyMap");
        keyMapArray.GetArrayLength().ShouldBe(96);
        foreach (var item in keyMapArray.EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            uint expectedVk = item.GetProperty("vk").GetUInt32();
            string variant = item.GetProperty("normalizationVariant").GetString()!;

            KeyNameMapper.TryMapToVk(key, out uint vk).ShouldBeTrue();
            vk.ShouldBe(expectedVk);

            KeyNameMapper.TryMapToVk(variant, out uint variantVk).ShouldBeTrue();
            variantVk.ShouldBe(expectedVk);
        }

        // 2. Verify enabledValidation (1536 cases)
        var validationArray = root.GetProperty("enabledValidation");
        validationArray.GetArrayLength().ShouldBe(1536);
        int validCount = 0;
        int invalidCount = 0;

        foreach (var item in validationArray.EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            int modifiers = item.GetProperty("modifiers").GetInt32();
            bool expectedPasses = item.GetProperty("passesInputValidation").GetBoolean();

            var binding = new HotkeyBinding((HotkeyModifiers)modifiers, key);
            bool actualPasses = KeyNameMapper.ValidateBinding(binding, out _);

            actualPasses.ShouldBe(expectedPasses, $"Failed for Key={key}, Modifiers={modifiers}");
            if (actualPasses) validCount++;
            else invalidCount++;
        }

        validCount.ShouldBe(1412);
        invalidCount.ShouldBe(124);

        // 3. Verify rejectedKeyStrings
        foreach (var item in root.GetProperty("rejectedKeyStrings").EnumerateArray())
        {
            string rejectedKey = item.GetString()!;
            KeyNameMapper.TryMapToVk(rejectedKey, out _).ShouldBeFalse();
        }

        // 4. Verify rejectedModifierMasks
        foreach (var item in root.GetProperty("rejectedModifierMasks").EnumerateArray())
        {
            int mask = item.GetInt32();
            var binding = new HotkeyBinding((HotkeyModifiers)mask, "A");
            KeyNameMapper.ValidateBinding(binding, out _).ShouldBeFalse();
        }
    }
}
