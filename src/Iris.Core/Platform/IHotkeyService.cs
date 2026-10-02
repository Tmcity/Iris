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
/// 全局热键，完整规则见 HOTKEY_SERVICE.md。构造无 I/O；首次有效启用注册由 App 在 UI 线程创建消息窗口并绑定所有者线程。
/// 创建后有资源的注册、注销和销毁必须在所有者线程；未初始化或已释放的 Dispose 幂等。
/// Register 先校验输入，再对同一 Action 先注销后注册；Enabled=false 为注销，实际成功或无记录才返回 Success。
/// 无效键/组合（含 F12）返回 HotkeyInvalid，系统冲突返回 HotkeyConflict，其它原生注册失败返回 Unknown。
/// 注销/销毁失败保留资源以供重试，Unregister/UnregisterAll/Dispose 抛 IrisException(Unknown)；无记录注销幂等。
/// Pressed 在所有者 UI 线程触发，只发布仍有效的注册；签名和设置格式不变。
/// </summary>
public interface IHotkeyService : IDisposable
{
    HotkeyRegistrationResult Register(IrisAction action, HotkeyBinding binding);

    void Unregister(IrisAction action);

    void UnregisterAll();

    event EventHandler<HotkeyPressedEventArgs>? Pressed;
}
