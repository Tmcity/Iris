using Iris.Core.Settings;

namespace Iris.Core.Platform;

public sealed record SelectionReadOptions(
    IReadOnlyList<SelectionReadMethod> Order,
    bool RestoreClipboard,
    int ClipboardTimeoutMs,
    IReadOnlyList<string> ClipboardUnsafeProcesses);

public sealed record SelectionReadResult(string Text, SelectionReadMethod Method, ForegroundAppInfo? SourceApp);

/// <summary>
/// 读取前台应用当前选中的文本。按 Order 依次尝试，某方法得到非空白文本即返回；全部失败返回 null。
/// Clipboard 方法要点：先等待用户松开热键修饰键（最多 500ms），快照剪贴板 → 清空 → SendInput(Ctrl+C) →
/// 轮询 GetClipboardSequenceNumber 直到变化或超时 → 读文本 → 按需恢复快照。
/// 前台进程在 ClipboardUnsafeProcesses 中时跳过 Clipboard 方法。UIAutomation 调用不得在 UI 线程执行。
/// </summary>
public interface ISelectionTextReader
{
    Task<SelectionReadResult?> ReadAsync(SelectionReadOptions options, CancellationToken cancellationToken);
}
