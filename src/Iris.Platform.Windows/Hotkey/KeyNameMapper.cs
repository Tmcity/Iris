using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Iris.Core.Settings;

namespace Iris.Platform.Windows.Hotkey;

/// <summary>
/// 按键名称与 Win32 Virtual-Key (VK) 映射及热键输入有效性校验。
/// 严格遵循 ARCH §10.3 / HOTKEY_SERVICE.md §2 规定的 96 键白名单。
/// </summary>
internal static class KeyNameMapper
{
    private const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win;

    private static readonly FrozenDictionary<string, uint> s_keyMap = CreateKeyMap();
    private static readonly FrozenDictionary<string, string> s_canonicalNames = CreateCanonicalNames();
    private static readonly FrozenDictionary<uint, string> s_vkMap = CreateVkMap();

    private static FrozenDictionary<string, uint> CreateKeyMap()
    {
        var map = new Dictionary<string, uint>(96, StringComparer.OrdinalIgnoreCase);

        // 1. A–Z (0x41–0x5A) -> 26 keys
        for (int i = 0; i < 26; i++)
        {
            char letter = (char)('A' + i);
            map.Add(letter.ToString(), (uint)(0x41 + i));
        }

        // 2. D0–D9 (0x30–0x39) -> 10 keys
        for (int i = 0; i <= 9; i++)
        {
            map.Add($"D{i}", (uint)(0x30 + i));
        }

        // 3. F1–F24 (0x70–0x87) -> 24 keys (F12 = 0x7B 可解析，注册时拦截)
        for (int i = 1; i <= 24; i++)
        {
            map.Add($"F{i}", (uint)(0x70 + (i - 1)));
        }

        // 4. NumPad0–NumPad9 (0x60–0x69) -> 10 keys
        for (int i = 0; i <= 9; i++)
        {
            map.Add($"NumPad{i}", (uint)(0x60 + i));
        }

        // 5. 导航与编辑键 -> 15 keys
        map.Add("Space", 0x20);
        map.Add("Enter", 0x0D);
        map.Add("Tab", 0x09);
        map.Add("Escape", 0x1B);
        map.Add("Insert", 0x2D);
        map.Add("Delete", 0x2E);
        map.Add("Home", 0x24);
        map.Add("End", 0x23);
        map.Add("PageUp", 0x21);
        map.Add("PageDown", 0x22);
        map.Add("Left", 0x25);
        map.Add("Up", 0x26);
        map.Add("Right", 0x27);
        map.Add("Down", 0x28);
        map.Add("Snapshot", 0x2C);

        // 6. OEM 键 -> 11 keys
        map.Add("OemTilde", 0xC0);
        map.Add("OemMinus", 0xBD);
        map.Add("OemPlus", 0xBB);
        map.Add("OemOpenBrackets", 0xDB);
        map.Add("OemCloseBrackets", 0xDD);
        map.Add("OemPipe", 0xDC);
        map.Add("OemSemicolon", 0xBA);
        map.Add("OemQuotes", 0xDE);
        map.Add("OemComma", 0xBC);
        map.Add("OemPeriod", 0xBE);
        map.Add("OemQuestion", 0xBF);

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, string> CreateCanonicalNames()
    {
        var dict = new Dictionary<string, string>(s_keyMap.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var key in s_keyMap.Keys)
        {
            dict.Add(key, key);
        }
        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<uint, string> CreateVkMap()
    {
        var dict = new Dictionary<uint, string>(s_keyMap.Count);
        foreach (var (key, vk) in s_keyMap)
        {
            dict[vk] = key;
        }
        return dict.ToFrozenDictionary();
    }

    /// <summary>
    /// 尝试将 Win32 Virtual-Key Code 映射为 96 键白名单中的规范化键名。
    /// 未知键返回 false，不回显原始数字。
    /// </summary>
    public static bool TryMapFromVk(uint vk, [NotNullWhen(true)] out string? canonicalKey)
    {
        return s_vkMap.TryGetValue(vk, out canonicalKey);
    }

    /// <summary>
    /// 尝试取得白名单键名的受限规范化表示。
    /// 仅当键名在 96 键白名单内时返回 true 并输出 canonical 键名，否则返回 false。
    /// 避免原始任意文本泄入 UI 提示或日志。
    /// </summary>
    public static bool TryGetCanonicalKeyName(string? keyName, [NotNullWhen(true)] out string? canonicalKey)
    {
        canonicalKey = null;
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        string trimmed = keyName.Trim();
        return s_canonicalNames.TryGetValue(trimmed, out canonicalKey);
    }

    /// <summary>
    /// 尝试将键名映射为 Win32 Virtual-Key Code。
    /// 允许首尾空白与大小写不敏感，拒绝纯数字、十六进制、未列出别名及组合键字符串。
    /// </summary>
    public static bool TryMapToVk(string? keyName, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        string trimmed = keyName.Trim();
        return s_keyMap.TryGetValue(trimmed, out vk);
    }

    /// <summary>
    /// 校验热键绑定是否满足输入规范与组合规则。
    /// 返回 true 时输出规范化 VK；返回 false 表示输入无效（HotkeyInvalid）。
    /// </summary>
    public static bool ValidateBinding(HotkeyBinding? binding, out uint vk)
    {
        vk = 0;
        if (binding == null)
        {
            return false;
        }

        if (!TryMapToVk(binding.Key, out vk))
        {
            return false;
        }

        // 仅允许 Alt/Control/Shift/Win 4 个位，未知位或负数均视为非法
        if ((binding.Modifiers & ~AllowedModifiers) != 0 || (int)binding.Modifiers < 0)
        {
            return false;
        }

        // F12 保留给系统调试器，禁止作为热键注册
        if (vk == 0x7B)
        {
            return false;
        }

        // 无修饰键时：仅允许单独 Snapshot (0x2C) 或 F13–F24 (0x7C–0x87)
        if (binding.Modifiers == HotkeyModifiers.None)
        {
            bool isStandaloneAllowed = vk == 0x2C || (vk >= 0x7C && vk <= 0x87);
            if (!isStandaloneAllowed)
            {
                return false;
            }
        }

        // 仅 Shift + 字母非法（避免拦截正常大写输入）
        if (binding.Modifiers == HotkeyModifiers.Shift && vk >= 0x41 && vk <= 0x5A)
        {
            return false;
        }

        return true;
    }
}
