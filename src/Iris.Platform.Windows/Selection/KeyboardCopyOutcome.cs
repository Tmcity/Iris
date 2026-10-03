namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 键盘模拟复制结果状态。
/// </summary>
internal enum KeyboardCopyOutcome
{
    /// <summary>四个按键事件已按顺序确认注入原生输入流。</summary>
    Injected,

    /// <summary>修饰键或 C 键在等待超时（500ms）后仍未完全释放。</summary>
    NotReady,

    /// <summary>目标窗口无效、非前台、属于本进程，或在发送中途前台目标发生变化。</summary>
    TargetChanged,

    /// <summary>已有活动操作正在进行，或尚有待释放的按键残留。</summary>
    Busy,

    /// <summary>原生输入调用失败（返回 0、抛出异常或异常返回值）。</summary>
    Failed
}
