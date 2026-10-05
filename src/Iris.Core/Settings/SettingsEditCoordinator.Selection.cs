using Iris.Core.Errors;

namespace Iris.Core.Settings;

internal sealed partial class SettingsEditCoordinator
{
    private static bool HasSelectionEdits(SelectionSettingsPatch? patch) => patch != null &&
        (patch.ReadOrder != null || patch.RestoreClipboard != null || patch.ClipboardTimeoutMs != null
         || patch.ClipboardUnsafeProcesses != null || patch.OpenInputWhenEmpty != null);

    private static SelectionSettingsPatch? CloneSelection(SelectionSettingsPatch? patch) => patch == null ? null : patch with
    {
        ReadOrder = patch.ReadOrder == null ? null : new(patch.ReadOrder.Base?.ToArray()!, patch.ReadOrder.Target?.ToArray()!),
        ClipboardUnsafeProcesses = patch.ClipboardUnsafeProcesses == null ? null
            : new(patch.ClipboardUnsafeProcesses.Base?.ToArray()!, patch.ClipboardUnsafeProcesses.Target?.ToArray()!)
    };

    private static ErrorInfo? ValidateSelection(SelectionSettingsPatch? patch)
    {
        if (patch == null) return null;
        if (patch.ReadOrder is { } order && (order.Target == null || order.Target.Count is < 1 or > 2
            || order.Target.Any(method => method is not SelectionReadMethod.UIAutomation and not SelectionReadMethod.Clipboard)
            || order.Target.Distinct().Count() != order.Target.Count)) return InvalidSelection();
        if (patch.ClipboardTimeoutMs is { Target: < 100 or > 2000 }) return InvalidSelection();
        if (patch.ClipboardUnsafeProcesses is { } names)
        {
            if (names.Target == null || names.Target.Count > 256) return InvalidSelection();
            foreach (string? raw in names.Target)
            {
                if (raw == null) return InvalidSelection();
                string name = raw.Trim();
                if (name.Length is < 1 or > 255 || name is "." or ".." || name.EndsWith('.')
                    || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return InvalidSelection();
            }
        }
        return null;
    }
    private static ErrorInfo InvalidSelection() => new(ErrorCode.InvalidArgument, "划词设置目标无效，请检查读取顺序、等待时间和进程文件名。");

    private static SelectionSettingsPatch? NormalizeSelection(SelectionSettingsPatch? patch) => patch?.ClipboardUnsafeProcesses is not { } edit
        ? patch : patch with { ClipboardUnsafeProcesses = edit with { Target = edit.Target.Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() } };

    private static bool SelectionOrderEqual(IReadOnlyList<SelectionReadMethod>? left, IReadOnlyList<SelectionReadMethod>? right) =>
        ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right));
    private static bool SelectionProcessesEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        ReferenceEquals(left, right) || (left != null && right != null && new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right));

    private static bool SelectionConflict<T>(SettingsFieldEdit<T>? edit, T current, Func<T, T, bool> equals) =>
        edit != null && !equals(current, edit.Base) && !equals(current, edit.Target);
    private static bool HasSelectionConflict(SelectionSettingsPatch? patch, SelectionSettings current) => patch != null &&
        (SelectionConflict(patch.ReadOrder, current.ReadOrder, SelectionOrderEqual)
         || SelectionConflict(patch.ClipboardUnsafeProcesses, current.ClipboardUnsafeProcesses, SelectionProcessesEqual)
         || SelectionConflict(patch.RestoreClipboard, current.RestoreClipboard, static (a, b) => a == b)
         || SelectionConflict(patch.ClipboardTimeoutMs, current.ClipboardTimeoutMs, static (a, b) => a == b)
         || SelectionConflict(patch.OpenInputWhenEmpty, current.OpenInputWhenEmpty, static (a, b) => a == b));

    private static SelectionSettings ApplySelection(SelectionSettingsPatch? patch, SelectionSettings current)
    {
        if (patch == null) return current;
        if (HasSelectionConflict(patch, current)) throw new StagingConflictException(new ErrorInfo(ErrorCode.Unknown, ConflictMessage));
        var order = patch.ReadOrder?.Target ?? current.ReadOrder;
        var processes = patch.ClipboardUnsafeProcesses?.Target ?? current.ClipboardUnsafeProcesses;
        bool restore = patch.RestoreClipboard?.Target ?? current.RestoreClipboard;
        int timeout = patch.ClipboardTimeoutMs?.Target ?? current.ClipboardTimeoutMs;
        bool input = patch.OpenInputWhenEmpty?.Target ?? current.OpenInputWhenEmpty;
        bool sameOrder = SelectionOrderEqual(order, current.ReadOrder), sameProcesses = SelectionProcessesEqual(processes, current.ClipboardUnsafeProcesses);
        if (sameOrder && sameProcesses && restore == current.RestoreClipboard && timeout == current.ClipboardTimeoutMs && input == current.OpenInputWhenEmpty) return current;
        return current with
        {
            ReadOrder = sameOrder ? current.ReadOrder : order,
            ClipboardUnsafeProcesses = sameProcesses ? current.ClipboardUnsafeProcesses : processes,
            RestoreClipboard = restore, ClipboardTimeoutMs = timeout, OpenInputWhenEmpty = input
        };
    }
}
