using Iris.Core.Errors;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 键盘模拟复制结果模型。
/// </summary>
internal sealed record KeyboardCopyResult(
    KeyboardCopyOutcome Outcome,
    bool MayHaveCopied = false,
    ErrorInfo? Error = null);
