namespace Iris.App.Settings;

internal sealed partial class SettingsViewModel
{
    // View-only drill-down state for the two list/detail pages. It is not part of the
    // draft: it is never saved, never counted by IsDirty and never touched by save,
    // reload or merge. Only the settings window's code-behind flips these flags.
    private bool _isTranslationDetailOpen;
    private bool _isOcrDetailOpen;

    public bool IsTranslationDetailOpen
    {
        get => _isTranslationDetailOpen;
        set
        {
            if (_isTranslationDetailOpen != value)
            {
                _isTranslationDetailOpen = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsOcrDetailOpen
    {
        get => _isOcrDetailOpen;
        set
        {
            if (_isOcrDetailOpen != value)
            {
                _isOcrDetailOpen = value;
                OnPropertyChanged();
            }
        }
    }
}
