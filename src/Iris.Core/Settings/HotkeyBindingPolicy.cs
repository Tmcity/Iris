using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Iris.Core.Settings;

/// <summary>
/// 纯 Core 快捷键输入与规范化策略（specs/HOTKEY_SETTINGS_EDITING.md §1–§2）。
/// 严格基于 96 键白名单与规范化规则，无原生、Win32、WPF 或 I/O 依赖。
/// </summary>
internal sealed class HotkeyBindingPolicy : IHotkeyBindingPolicy
{
    private const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win;

    private static readonly FrozenDictionary<string, string> s_canonicalNames = CreateCanonicalNames();

    private static FrozenDictionary<string, string> CreateCanonicalNames()
    {
        var dict = new Dictionary<string, string>(96, StringComparer.OrdinalIgnoreCase);

        // 1. A–Z (26 keys)
        for (int i = 0; i < 26; i++)
        {
            string letter = ((char)('A' + i)).ToString();
            dict.Add(letter, letter);
        }

        // 2. D0–D9 (10 keys)
        for (int i = 0; i <= 9; i++)
        {
            dict.Add($"D{i}", $"D{i}");
        }

        // 3. F1–F24 (24 keys)
        for (int i = 1; i <= 24; i++)
        {
            dict.Add($"F{i}", $"F{i}");
        }

        // 4. NumPad0–NumPad9 (10 keys)
        for (int i = 0; i <= 9; i++)
        {
            dict.Add($"NumPad{i}", $"NumPad{i}");
        }

        // 5. 导航与编辑键 (15 keys)
        string[] navigationKeys =
        [
            "Space", "Enter", "Tab", "Escape", "Insert", "Delete",
            "Home", "End", "PageUp", "PageDown",
            "Left", "Up", "Right", "Down", "Snapshot"
        ];
        foreach (var key in navigationKeys)
        {
            dict.Add(key, key);
        }

        // 6. OEM 键 (11 keys)
        string[] oemKeys =
        [
            "OemTilde", "OemMinus", "OemPlus", "OemOpenBrackets", "OemCloseBrackets",
            "OemPipe", "OemSemicolon", "OemQuotes", "OemComma", "OemPeriod", "OemQuestion"
        ];
        foreach (var key in oemKeys)
        {
            dict.Add(key, key);
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryNormalize(HotkeyBinding? binding, [NotNullWhen(true)] out HotkeyBinding? normalized)
    {
        normalized = null;
        if (binding == null)
        {
            return false;
        }

        // 仅允许 Alt/Control/Shift/Win 4 个位，未知位或负数均视为非法
        if ((binding.Modifiers & ~AllowedModifiers) != 0 || (int)binding.Modifiers < 0)
        {
            return false;
        }

        if (!binding.Enabled)
        {
            // Enabled=false: 空白/null Key 规范化为 (None, "", false)
            if (string.IsNullOrWhiteSpace(binding.Key))
            {
                normalized = new HotkeyBinding(HotkeyModifiers.None, string.Empty, false);
                return true;
            }

            // 非空 Key 必须在 96 键白名单内，modifiers 合法即可，不要求组合可注册
            string trimmedKey = binding.Key.Trim();
            if (!s_canonicalNames.TryGetValue(trimmedKey, out var canonicalDisabledKey))
            {
                return false;
            }

            normalized = new HotkeyBinding(binding.Modifiers, canonicalDisabledKey, false);
            return true;
        }

        // Enabled=true: 空白 Key 拒绝
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return false;
        }

        string trimmed = binding.Key.Trim();
        if (!s_canonicalNames.TryGetValue(trimmed, out var canonicalKey))
        {
            return false;
        }

        // F12 保留给系统调试器，禁止作为热键注册
        if (string.Equals(canonicalKey, "F12", StringComparison.Ordinal))
        {
            return false;
        }

        // 无修饰键时：仅允许单独 Snapshot 或 F13–F24
        if (binding.Modifiers == HotkeyModifiers.None)
        {
            bool isStandaloneAllowed = string.Equals(canonicalKey, "Snapshot", StringComparison.Ordinal) ||
                                       IsF13ToF24(canonicalKey);
            if (!isStandaloneAllowed)
            {
                return false;
            }
        }

        // 仅 Shift + 字母非法（避免拦截正常大写输入）
        if (binding.Modifiers == HotkeyModifiers.Shift && IsLetter(canonicalKey))
        {
            return false;
        }

        normalized = new HotkeyBinding(binding.Modifiers, canonicalKey, true);
        return true;
    }

    private static bool IsF13ToF24(string key)
    {
        if (key.Length is < 3 or > 3 || key[0] != 'F')
        {
            return false;
        }

        if (int.TryParse(key.AsSpan(1), out int num))
        {
            return num is >= 13 and <= 24;
        }

        return false;
    }

    private static bool IsLetter(string key)
    {
        return key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z';
    }
}
