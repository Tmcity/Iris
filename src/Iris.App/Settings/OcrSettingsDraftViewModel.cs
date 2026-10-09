using System.ComponentModel;
using System.Globalization;
using Iris.Core.Ocr;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed record OcrModeOption(BaiduOcrMode Value, string Label);
internal sealed record OcrLayoutOption(OcrTextLayout Value, string Label);
internal sealed record OcrCredentialOption(CredentialEditKind Value, string Label);

internal sealed class OcrSettingsDraftViewModel : INotifyPropertyChanged
{
    public DraftValue<string> ActiveProviderId { get; }
    public DraftValue<string?> FallbackProviderId { get; }
    public DraftValue<OcrTextLayout> TextLayout { get; }
    public DraftValue<string> LanguageHint { get; }
    public DraftValue<bool> AutoCopyOnOcr { get; }
    public DraftValue<bool> SilentOcrShowNotification { get; }
    public string? ErrorMessage { get; private set; }
    public static IReadOnlyList<OcrLayoutOption> Layouts =>        [new(OcrTextLayout.KeepLines, UiText.OcrKeepLines), new(OcrTextLayout.MergeParagraphs, UiText.OcrMergeParagraphs)];    public OcrSettingsDraftViewModel(OcrSettings current, Func<bool> canEdit, Action changed)
    {
        ActiveProviderId = new(current.ActiveProviderId, canEdit, changed);
        FallbackProviderId = new(current.FallbackProviderId, canEdit, changed);
        TextLayout = new(current.TextLayout, canEdit, changed);
        LanguageHint = new(current.LanguageHint, canEdit, changed);
        AutoCopyOnOcr = new(current.AutoCopyOnOcr, canEdit, changed);
        SilentOcrShowNotification = new(current.SilentOcrShowNotification, canEdit, changed);
    }
    public bool IsDirty => ActiveProviderId.IsDirty || FallbackProviderId.IsDirty || TextLayout.IsDirty
        || LanguageHint.IsDirty || AutoCopyOnOcr.IsDirty || SilentOcrShowNotification.IsDirty;
    public bool TryCapture(IReadOnlyList<OcrProfileDraftViewModel> profiles, bool profilesDirty, out OcrSettingsPatch? patch)
    {
        patch = null; ErrorMessage = null;
        if ((IsDirty || profilesDirty) && (!profiles.Any(p => p.Id == ActiveProviderId.Value)
            || FallbackProviderId.Value != null && (FallbackProviderId.Value == ActiveProviderId.Value || !profiles.Any(p => p.Id == FallbackProviderId.Value))))
            ErrorMessage = UiText.OcrProviderSelectionError;
        if (TextLayout.IsDirty && !Enum.IsDefined(TextLayout.Value)) ErrorMessage = UiText.OcrLayoutError;
        PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
        if (ErrorMessage != null) return false;
        if (IsDirty) patch = new(ActiveProviderId.Edit(), FallbackProviderId.Edit(), TextLayout.Edit(), LanguageHint.Edit(), AutoCopyOnOcr.Edit(), SilentOcrShowNotification.Edit());
        return true;
    }
    public void Merge(OcrSettings current, bool reset)
    {
        ActiveProviderId.Merge(current.ActiveProviderId, reset); FallbackProviderId.Merge(current.FallbackProviderId, reset);
        TextLayout.Merge(current.TextLayout, reset); LanguageHint.Merge(current.LanguageHint, reset);
        AutoCopyOnOcr.Merge(current.AutoCopyOnOcr, reset); SilentOcrShowNotification.Merge(current.SilentOcrShowNotification, reset);
        if (reset) { ErrorMessage = null; PropertyChanged?.Invoke(this, new(nameof(ErrorMessage))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class ScreenshotSettingsDraftViewModel : INotifyPropertyChanged
{
    public DraftValue<bool> ConfirmOnMouseUp { get; }
    public DraftValue<bool> ShowMagnifier { get; }
    public DraftValue<bool> HideIrisWindows { get; }
    public DraftValue<bool> CopyImageToClipboard { get; }
    public DraftValue<string> MaskOpacityText { get; }
    private double _baseOpacity;
    public string? ErrorMessage { get; private set; }
    public ScreenshotSettingsDraftViewModel(ScreenshotSettings current, Func<bool> canEdit, Action changed)
    {
        ConfirmOnMouseUp = new(current.ConfirmOnMouseUp, canEdit, changed); ShowMagnifier = new(current.ShowMagnifier, canEdit, changed);
        HideIrisWindows = new(current.HideIrisWindows, canEdit, changed); CopyImageToClipboard = new(current.CopyImageToClipboard, canEdit, changed);
        _baseOpacity = current.MaskOpacity; MaskOpacityText = new(current.MaskOpacity.ToString(CultureInfo.InvariantCulture), canEdit, changed);
    }
    public bool IsDirty => ConfirmOnMouseUp.IsDirty || ShowMagnifier.IsDirty || HideIrisWindows.IsDirty || CopyImageToClipboard.IsDirty || MaskOpacityText.IsDirty;
    public bool TryCapture(out ScreenshotSettingsPatch? patch)
    {
        patch = null; ErrorMessage = null; double opacity = _baseOpacity;
        if (MaskOpacityText.IsDirty && (!double.TryParse(MaskOpacityText.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out opacity)
            || !double.IsFinite(opacity) || opacity is < 0 or > 0.9)) ErrorMessage = UiText.ScreenshotOpacityError;
        PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
        if (ErrorMessage != null) return false;
        if (IsDirty) patch = new(ConfirmOnMouseUp.Edit(), ShowMagnifier.Edit(), HideIrisWindows.Edit(), CopyImageToClipboard.Edit(),
            MaskOpacityText.IsDirty ? new(_baseOpacity, opacity) : null);
        return true;
    }
    public void Merge(ScreenshotSettings current, bool reset)
    {
        ConfirmOnMouseUp.Merge(current.ConfirmOnMouseUp, reset); ShowMagnifier.Merge(current.ShowMagnifier, reset);
        HideIrisWindows.Merge(current.HideIrisWindows, reset); CopyImageToClipboard.Merge(current.CopyImageToClipboard, reset);
        if (reset || !MaskOpacityText.IsDirty) { _baseOpacity = current.MaskOpacity; MaskOpacityText.Merge(current.MaskOpacity.ToString(CultureInfo.InvariantCulture), reset); }
        if (reset) { ErrorMessage = null; PropertyChanged?.Invoke(this, new(nameof(ErrorMessage))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
