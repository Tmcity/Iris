using Iris.Core.Settings;

namespace Iris.Core.Platform;

public sealed record SelectionReadOptions(
    IReadOnlyList<SelectionReadMethod> Order,
    bool RestoreClipboard,
    int ClipboardTimeoutMs,
    IReadOnlyList<string> ClipboardUnsafeProcesses);

public sealed record SelectionReadResult(string Text, SelectionReadMethod Method, ForegroundAppInfo? SourceApp)
{
    public override string ToString() => $"SelectionReadResult {{ Method={Method} }}";
}

/// <summary>
/// 读取本次已确认前台来源的选区，完整规则见 SELECTION_READING.md。按 Order 尝试，普通无结果返回 null。
/// Clipboard 在修饰键释放后准备快照/条件清空，复核安全焦点后复制，按来源和版本接受文本并条件恢复。
/// 敏感或未通过复制安全许可的控件不得因 fallback 而注入按键；sequence/owner 不提供严格按键因果证明。
/// 结果最多 65,536 UTF-16 code units；无效参数、超限、忙和清理失败抛安全 IrisException。
/// 取消在真实工作及必要清理后抛 OperationCanceledException；清理失败保留责任，不降为 null/超时。
/// 平台工作不在 UI 线程执行，调用完成覆盖实际收尾；响应预算不包含任意同步 native 调用的硬终止保证。
/// </summary>
public interface ISelectionTextReader
{
    Task<SelectionReadResult?> ReadAsync(SelectionReadOptions options, CancellationToken cancellationToken);
}
