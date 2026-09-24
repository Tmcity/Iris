namespace Iris.Core.Settings;

/// <summary>数值与 Win32 MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN 一致。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <param name="Key">WPF System.Windows.Input.Key 枚举名，如 "D"、"F1"、"D1"、"Space"、"Snapshot"。Platform 层负责转换为 VK。</param>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key, bool Enabled = true);
