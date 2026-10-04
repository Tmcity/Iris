namespace Iris.Platform.Windows.Selection;

/// <summary>
/// UIA 选区读取结果状态（内部枚举）。
/// 严格区分各终端与边缘状态，禁止将 Sensitive/TooLong/TargetChanged/Busy 误判为普通空选区。
/// </summary>
internal enum UiaSelectionStatus
{
    Success,
    Empty,
    Unsupported,
    Sensitive,
    TooLong,
    TimedOut,
    TargetChanged,
    Busy,
    Failed
}
