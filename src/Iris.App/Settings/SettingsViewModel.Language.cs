using Iris.App.Resources;

namespace Iris.App.Settings;

internal sealed record UiLanguageChoice(string Value, string Name);

internal sealed partial class SettingsViewModel
{
    public DraftValue<string> InterfaceLanguage { get; private set; } = null!;
    public static IReadOnlyList<UiLanguageChoice> InterfaceLanguages =>
        [new("zh-CN", UiText.LanguageChineseNative), new("en-US", UiText.LanguageEnglishNative)];
    public string InterfaceLanguageHint => _settingsService.Current.General.UiLanguage != AppLanguage.Name
        ? UiText.LanguageRestartPending : UiText.LanguageNextStart;
    private void InitializeInterfaceLanguage() => InterfaceLanguage = new(_settingsService.Current.General.UiLanguage, () => CanEdit,
        () => { OnPropertyChanged(nameof(IsDirty)); });
    private void MergeInterfaceLanguage(bool reset = false)
    {
        InterfaceLanguage.Merge(_settingsService.Current.General.UiLanguage, reset);
        OnPropertyChanged(nameof(InterfaceLanguageHint));
    }
}
