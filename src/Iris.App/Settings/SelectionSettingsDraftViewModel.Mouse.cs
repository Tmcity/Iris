using System.Globalization;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed record MouseModeOption(MouseSelectionMode Mode, string Label);

internal sealed partial class SelectionSettingsDraftViewModel
{
    private MouseSelectionMode _baseMode, _mode;
    private bool _baseBeam, _beam;
    private int _baseDrag, _baseHide;
    private string _drag = "", _hide = "", _excluded = "", _baseExcludedText = "";
    private IReadOnlyList<string>? _baseExcluded;
    public static IReadOnlyList<MouseModeOption> MouseModes =>    [new(MouseSelectionMode.Off, UiText.MouseModeOff), new(MouseSelectionMode.ShowIcon, UiText.MouseModeIcon), new(MouseSelectionMode.AutoTranslate, UiText.MouseModeAuto)];    public MouseSelectionMode MouseMode { get => _mode; set { if (!CanChange || _mode == value) return; _mode = value; Changed(nameof(MouseMode)); } }
    public bool RequireIBeamCursor { get => _beam; set { if (!CanChange || _beam == value) return; _beam = value; Changed(nameof(RequireIBeamCursor)); } }
    public string MinDragDistanceText { get => _drag; set { if (!CanChange || _drag == value) return; _drag = value ?? ""; Changed(nameof(MinDragDistanceText)); } }
    public string IconAutoHideText { get => _hide; set { if (!CanChange || _hide == value) return; _hide = value ?? ""; Changed(nameof(IconAutoHideText)); } }
    public string ExcludedProcessesText { get => _excluded; set { if (!CanChange) return; var text = (value ?? "").ReplaceLineEndings("\n"); if (text == _excluded) return; _excluded = text; Changed(nameof(ExcludedProcessesText)); } }
    private bool DragDirty => _drag != _baseDrag.ToString(CultureInfo.InvariantCulture);
    private bool HideDirty => _hide != _baseHide.ToString(CultureInfo.InvariantCulture);
    private bool ExcludedDirty => _excluded != _baseExcludedText;
    private bool MouseIsDirty => _mode != _baseMode || _beam != _baseBeam || DragDirty || HideDirty || ExcludedDirty;
    private bool CaptureMouse(out SelectionSettingsPatch patch)
    {
        patch = new();
        if (_mode != _baseMode && !Enum.IsDefined(_mode)) return Invalid(UiText.MouseModeError);
        int drag = _baseDrag, hide = _baseHide;
        if (DragDirty && (!int.TryParse(_drag, NumberStyles.Integer, CultureInfo.InvariantCulture, out drag) || drag is < 2 or > 50)) return Invalid(UiText.MouseDragError);
        if (HideDirty && (!int.TryParse(_hide, NumberStyles.Integer, CultureInfo.InvariantCulture, out hide) || hide is < 500 or > 10000)) return Invalid(UiText.MouseHideError);
        string[] names = _excluded.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (ExcludedDirty && (names.Length > 256 || names.Any(raw =>
        {
            string name = raw.Trim();
            return name.Length is < 1 or > 255 || name is "." or ".." || name.EndsWith('.') || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));
        }))) return Invalid(UiText.SelectionProcessesError);
        patch = new()
        {
            MouseMode = _mode != _baseMode ? new(_baseMode, _mode) : null,
            RequireIBeamCursor = _beam != _baseBeam ? new(_baseBeam, _beam) : null,
            MinDragDistancePx = DragDirty ? new(_baseDrag, drag) : null, IconAutoHideMs = HideDirty ? new(_baseHide, hide) : null,
            ExcludedProcesses = ExcludedDirty ? new(_baseExcluded?.ToArray()!, names) : null,
        };
        return true;
    }
    private void MergeMouse(SelectionSettings current, bool reset)
    {
        if (reset || _mode == _baseMode) _mode = _baseMode = current.MouseMode;
        if (reset || _beam == _baseBeam) _beam = _baseBeam = current.RequireIBeamCursor;
        if (reset || !DragDirty) { _baseDrag = current.MinDragDistancePx; _drag = _baseDrag.ToString(CultureInfo.InvariantCulture); }
        if (reset || !HideDirty) { _baseHide = current.IconAutoHideMs; _hide = _baseHide.ToString(CultureInfo.InvariantCulture); }
        if (reset || !ExcludedDirty)
        {
            _baseExcluded = current.ExcludedProcesses?.ToArray();
            _excluded = _baseExcludedText = string.Join('\n', _baseExcluded ?? []).ReplaceLineEndings("\n");
        }
    }
}
