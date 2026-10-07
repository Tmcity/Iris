using System.ComponentModel;
using System.Globalization;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed class HistorySettingsDraftViewModel : INotifyPropertyChanged
{
    private int _baseMax;
    public DraftValue<bool> Enabled { get; }
    public DraftValue<string> MaxEntriesText { get; }
    public string? ErrorMessage { get; private set; }
    public bool IsDirty => Enabled.IsDirty || MaxEntriesText.IsDirty;

    public HistorySettingsDraftViewModel(HistorySettings current, Func<bool> canEdit, Action changed)
    {
        Enabled = new(current.Enabled, canEdit, changed);
        _baseMax = current.MaxEntries;
        MaxEntriesText = new(_baseMax.ToString(CultureInfo.InvariantCulture), canEdit, changed);
    }

    public bool TryCapture(out HistorySettingsPatch? patch)
    {
        patch = null;
        ErrorMessage = null;
        int max = _baseMax;
        if (MaxEntriesText.IsDirty && (!int.TryParse(MaxEntriesText.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out max)
            || max is < 100 or > 100000)) ErrorMessage = UiText.HistoryMaxError;
        PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
        if (ErrorMessage != null) return false;
        if (IsDirty) patch = new(Enabled.Edit(), MaxEntriesText.IsDirty ? new(_baseMax, max) : null);
        return true;
    }

    public void Merge(HistorySettings current, bool reset = false)
    {
        Enabled.Merge(current.Enabled, reset);
        if (reset || !MaxEntriesText.IsDirty)
        {
            _baseMax = current.MaxEntries;
            MaxEntriesText.Merge(_baseMax.ToString(CultureInfo.InvariantCulture), reset);
        }
        if (reset) { ErrorMessage = null; PropertyChanged?.Invoke(this, new(nameof(ErrorMessage))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
