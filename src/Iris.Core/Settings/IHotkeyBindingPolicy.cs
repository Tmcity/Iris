namespace Iris.Core.Settings;

/// <summary>纯输入规则，无 I/O 或原生调用；完整语义见 HOTKEY_SETTINGS_EDITING.md。</summary>
public interface IHotkeyBindingPolicy
{
    /// <summary>成功返回规范化绑定；禁用空键输出 (None, "", false)。非法输入返回 false 和 null。</summary>
    bool TryNormalize(HotkeyBinding? binding, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HotkeyBinding? normalized);
}
