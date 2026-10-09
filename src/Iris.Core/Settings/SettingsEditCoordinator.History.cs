using Iris.Core.Errors;

namespace Iris.Core.Settings;

internal sealed partial class SettingsEditCoordinator
{
    private static bool HasHistoryEdits(HistorySettingsPatch? patch) => patch is { Enabled: not null } or { MaxEntries: not null };

    private static ErrorInfo? ValidateHistory(HistorySettingsPatch? patch) =>
        patch?.MaxEntries is { Base: < 100 or > 100000 } or { Target: < 100 or > 100000 }
            ? new(ErrorCode.InvalidArgument, global::Iris.Core.Localization.CoreText.Get("Text_9f4aeb1e7d83")) : null;

    private static bool HasHistoryConflict(HistorySettingsPatch? patch, HistorySettings current) => patch != null &&
        (SelectionConflict(patch.Enabled, current.Enabled, static (a, b) => a == b)
         || SelectionConflict(patch.MaxEntries, current.MaxEntries, static (a, b) => a == b));

    private static HistorySettings ApplyHistory(HistorySettingsPatch? patch, HistorySettings current)
    {
        if (patch == null) return current;
        if (HasHistoryConflict(patch, current)) throw new StagingConflictException(new(ErrorCode.Unknown, ConflictMessage));
        bool enabled = patch.Enabled?.Target ?? current.Enabled;
        int max = patch.MaxEntries?.Target ?? current.MaxEntries;
        return enabled == current.Enabled && max == current.MaxEntries ? current : current with { Enabled = enabled, MaxEntries = max };
    }
}
