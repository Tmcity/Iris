using Iris.Core.Errors;

namespace Iris.Core.Settings;

internal sealed partial class SettingsEditCoordinator
{
    private static bool HasSelectionEdits(SelectionSettingsPatch? patch) => patch != null &&
        (patch.ReadOrder != null || patch.RestoreClipboard != null || patch.ClipboardTimeoutMs != null
         || patch.ClipboardUnsafeProcesses != null || patch.OpenInputWhenEmpty != null || patch.MouseMode != null
         || patch.RequireIBeamCursor != null || patch.MinDragDistancePx != null || patch.IconAutoHideMs != null || patch.ExcludedProcesses != null);

    private static SelectionSettingsPatch? CloneSelection(SelectionSettingsPatch? patch) => patch == null ? null : patch with
    {
        ReadOrder = patch.ReadOrder == null ? null : new(patch.ReadOrder.Base?.ToArray()!, patch.ReadOrder.Target?.ToArray()!),
        ClipboardUnsafeProcesses = patch.ClipboardUnsafeProcesses == null ? null
            : new(patch.ClipboardUnsafeProcesses.Base?.ToArray()!, patch.ClipboardUnsafeProcesses.Target?.ToArray()!),
        ExcludedProcesses = patch.ExcludedProcesses == null ? null : new(patch.ExcludedProcesses.Base?.ToArray()!, patch.ExcludedProcesses.Target?.ToArray()!)
    };

    private static ErrorInfo? ValidateSelection(SelectionSettingsPatch? patch)
    {
        if (patch == null) return null;
        if (patch.ReadOrder is { } order && (order.Target == null || order.Target.Count is < 1 or > 2
            || order.Target.Any(method => method is not SelectionReadMethod.UIAutomation and not SelectionReadMethod.Clipboard)
            || order.Target.Distinct().Count() != order.Target.Count)) return InvalidSelection();
        if (patch.ClipboardTimeoutMs is { Target: < 100 or > 2000 }) return InvalidSelection();
        if (patch.MinDragDistancePx is { Target: < 2 or > 50 } || patch.IconAutoHideMs is { Target: < 500 or > 10000 }) return InvalidSelection();
        if (patch.MouseMode is { } mode && !Enum.IsDefined(mode.Target)) return InvalidSelection();
        foreach (var names in new[] { patch.ClipboardUnsafeProcesses, patch.ExcludedProcesses })
        {
            if (names == null) continue;
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

    private static SelectionSettingsPatch? NormalizeSelection(SelectionSettingsPatch? patch) => patch == null ? null : patch with
    {
        ClipboardUnsafeProcesses = NormalizeProcesses(patch.ClipboardUnsafeProcesses), ExcludedProcesses = NormalizeProcesses(patch.ExcludedProcesses),
    };
    private static SettingsFieldEdit<IReadOnlyList<string>>? NormalizeProcesses(SettingsFieldEdit<IReadOnlyList<string>>? edit) => edit == null ? null
        : edit with { Target = edit.Target.Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };

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
         || SelectionConflict(patch.OpenInputWhenEmpty, current.OpenInputWhenEmpty, static (a, b) => a == b)
         || SelectionConflict(patch.MouseMode, current.MouseMode, static (a, b) => a == b)
         || SelectionConflict(patch.RequireIBeamCursor, current.RequireIBeamCursor, static (a, b) => a == b)
         || SelectionConflict(patch.MinDragDistancePx, current.MinDragDistancePx, static (a, b) => a == b)
         || SelectionConflict(patch.IconAutoHideMs, current.IconAutoHideMs, static (a, b) => a == b)
         || SelectionConflict(patch.ExcludedProcesses, current.ExcludedProcesses, SelectionProcessesEqual));

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
        var mouse = patch.MouseMode?.Target ?? current.MouseMode;
        bool beam = patch.RequireIBeamCursor?.Target ?? current.RequireIBeamCursor;
        int drag = patch.MinDragDistancePx?.Target ?? current.MinDragDistancePx, hide = patch.IconAutoHideMs?.Target ?? current.IconAutoHideMs;
        var excluded = patch.ExcludedProcesses?.Target ?? current.ExcludedProcesses;
        bool sameExcluded = SelectionProcessesEqual(excluded, current.ExcludedProcesses);
        if (sameOrder && sameProcesses && restore == current.RestoreClipboard && timeout == current.ClipboardTimeoutMs && input == current.OpenInputWhenEmpty
            && mouse == current.MouseMode && beam == current.RequireIBeamCursor && drag == current.MinDragDistancePx && hide == current.IconAutoHideMs && sameExcluded) return current;
        return current with
        {
            ReadOrder = sameOrder ? current.ReadOrder : order,
            ClipboardUnsafeProcesses = sameProcesses ? current.ClipboardUnsafeProcesses : processes,
            RestoreClipboard = restore, ClipboardTimeoutMs = timeout, OpenInputWhenEmpty = input,
            MouseMode = mouse, RequireIBeamCursor = beam, MinDragDistancePx = drag, IconAutoHideMs = hide,
            ExcludedProcesses = sameExcluded ? current.ExcludedProcesses : excluded,
        };
    }
}
