namespace Iris.App.Settings;

internal sealed partial class SettingsViewModel
{
    public DraftValue<bool> CheckForUpdates { get; private set; } = null!;
    private void InitializeUpdatePreference() => CheckForUpdates = new(_settingsService.Current.General.CheckForUpdates,
        () => CanEdit, () => { OnPropertyChanged(nameof(IsDirty)); RaiseAllCommandsCanExecute(); });
    private void MergeUpdatePreference(bool reset = false) => CheckForUpdates.Merge(_settingsService.Current.General.CheckForUpdates, reset);
}
