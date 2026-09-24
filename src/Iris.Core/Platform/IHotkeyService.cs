using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Settings;

namespace Iris.Core.Platform;

public sealed record HotkeyRegistrationResult(IrisAction Action, bool Success, ErrorCode? Error = null, string? Message = null);

public sealed class HotkeyPressedEventArgs(IrisAction action) : EventArgs
{
    public IrisAction Action { get; } = action;
}

/// <summary>
/// 全局热键（RegisterHotKey + 隐藏消息窗口，必须在 UI 线程创建与调用）。
/// Register 对同一 Action 重复调用 = 先注销再注册。Binding.Enabled=false 等价于 Unregister 并返回 Success。
/// 被其他程序占用 → Error=HotkeyConflict；Key 无法解析 → HotkeyInvalid。Pressed 在 UI 线程触发。
/// </summary>
public interface IHotkeyService : IDisposable
{
    HotkeyRegistrationResult Register(IrisAction action, HotkeyBinding binding);

    void Unregister(IrisAction action);

    void UnregisterAll();

    event EventHandler<HotkeyPressedEventArgs>? Pressed;
}
