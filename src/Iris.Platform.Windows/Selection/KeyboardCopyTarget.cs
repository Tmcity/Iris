namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 目标窗口句柄与进程 ID。不包含窗口标题或选区文本。
/// </summary>
internal readonly record struct KeyboardCopyTarget(nint WindowHandle, uint ProcessId)
{
    public override string ToString() => $"[KeyboardCopyTarget PID={ProcessId}]";
}
