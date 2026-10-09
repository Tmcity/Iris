using System.ComponentModel;
using System.Globalization;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed class OcrProfileDraftViewModel : INotifyPropertyChanged
{
    private Func<bool> _canEdit;
    private Action<OcrProfileDraftViewModel> _edited;
    private OcrProviderProfile _initial;
    private string? _apiKey, _secretKey;
    public OcrProviderProfile? BaseProfile { get; private set; }
    public string Id => _initial.Id;
    public OcrProviderKind Kind => _initial.Kind;
    public bool IsBaidu => Kind == OcrProviderKind.BaiduCloud;
    public bool IsWindows => Kind == OcrProviderKind.WindowsLocal;
    public bool IsBuiltIn => string.Equals(Id, BuiltInProviderIds.BaiduOcr, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Id, BuiltInProviderIds.WindowsOcr, StringComparison.OrdinalIgnoreCase);
    public bool IsNew => BaseProfile == null;
    public long Revision { get; private set; }
    public string KindLabel => IsBaidu ? UiText.OcrBaidu : UiText.OcrWindows;
    public string CredentialDescription => IsBaidu ? UiText.OcrCredentialDescription : UiText.OcrLocalCredentialDescription;
    public DraftValue<string> DisplayName { get; }
    public DraftValue<bool> Enabled { get; }
    public DraftValue<string> TimeoutText { get; }
    public DraftValue<BaiduOcrMode> Mode { get; }
    public DraftValue<bool> DetectDirection { get; }
    public DraftValue<bool> Paragraph { get; }
    public DraftValue<string?> LanguageType { get; }
    public DraftValue<string> BaseUrl { get; }
    public DraftValue<string?> LanguageTag { get; }
    public DraftValue<CredentialEditKind> Credentials { get; }
    public string? ErrorMessage { get; private set; }
    public string? TestMessage { get; private set; }
    internal string? ReplacementApiKey => _apiKey;
    internal string? ReplacementSecretKey => _secretKey;
    public bool IsReplacing => IsBaidu && Credentials.Value == CredentialEditKind.Replace;
    public static IReadOnlyList<OcrModeOption> Modes =>    [new(BaiduOcrMode.GeneralBasic, UiText.OcrModeGeneralBasic), new(BaiduOcrMode.AccurateBasic, UiText.OcrModeAccurateBasic),
     new(BaiduOcrMode.General, UiText.OcrModeGeneral), new(BaiduOcrMode.Accurate, UiText.OcrModeAccurate)];
    public IReadOnlyList<OcrCredentialOption> CredentialOptions => IsBaidu ? AllCredentials : LocalCredentials;
    private static OcrCredentialOption[] AllCredentials =>
        [new(CredentialEditKind.Keep, UiText.OcrCredentialKeep), new(CredentialEditKind.Replace, UiText.OcrCredentialReplace), new(CredentialEditKind.Clear, UiText.OcrCredentialClear)];
    private static OcrCredentialOption[] LocalCredentials => [AllCredentials[0], AllCredentials[2]];

    internal OcrProfileDraftViewModel(OcrProviderProfile profile, bool isNew, Func<bool> canEdit, Action<OcrProfileDraftViewModel> edited)
    {
        _canEdit = canEdit; _edited = edited; _initial = Clone(profile); BaseProfile = isNew ? null : Clone(profile);
        var b = profile.BaiduOptions ?? new(); var w = profile.WindowsOptions ?? new();
        DisplayName = new(profile.DisplayName, canEdit, Changed); Enabled = new(profile.Enabled, canEdit, Changed);
        TimeoutText = new(profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), canEdit, Changed);
        Mode = new(b.Mode, canEdit, Changed); DetectDirection = new(b.DetectDirection, canEdit, Changed); Paragraph = new(b.Paragraph, canEdit, Changed);
        LanguageType = new(b.LanguageType, canEdit, Changed); BaseUrl = new(b.BaseUrl, canEdit, Changed); LanguageTag = new(w.LanguageTag, canEdit, Changed);
        Credentials = new(CredentialEditKind.Keep, canEdit, Changed);
    }
    public bool IsDirty => DisplayName.IsDirty || Enabled.IsDirty || TimeoutText.IsDirty || Mode.IsDirty || DetectDirection.IsDirty || Paragraph.IsDirty
        || LanguageType.IsDirty || BaseUrl.IsDirty || LanguageTag.IsDirty || Credentials.IsDirty || _apiKey != null || _secretKey != null;

    public void SetReplacementCredentials(string? apiKey, string? secretKey)
    {
        if (!_canEdit() || !IsReplacing || _apiKey == apiKey && _secretKey == secretKey) return;
        _apiKey = apiKey; _secretKey = secretKey; Changed();
    }
    public void ClearSecrets()
    {
        _apiKey = null; _secretKey = null; Credentials.Merge(CredentialEditKind.Keep, true);
        PropertyChanged?.Invoke(this, new(nameof(IsReplacing)));
    }
    public void Detach() { ClearSecrets(); _canEdit = static () => false; _edited = static _ => { }; }
    public void InvalidateTest() { Revision++; TestMessage = null; PropertyChanged?.Invoke(this, new(nameof(TestMessage))); }
    private void Changed()
    {
        if (Credentials != null && Credentials.Value != CredentialEditKind.Replace) { _apiKey = null; _secretKey = null; }
        ErrorMessage = null; InvalidateTest();
        PropertyChanged?.Invoke(this, new(nameof(IsDirty))); PropertyChanged?.Invoke(this, new(nameof(ErrorMessage))); PropertyChanged?.Invoke(this, new(nameof(IsReplacing)));
        _edited(this);
    }
    public bool Validate()
    {
        ErrorMessage = string.IsNullOrWhiteSpace(DisplayName.Value) ? UiText.OcrNameError : null;
        if (!int.TryParse(TimeoutText.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int timeout) || timeout is < 5 or > 120) ErrorMessage = UiText.OcrTimeoutError;
        if (IsBaidu && (!Enum.IsDefined(Mode.Value) || !Uri.TryCreate(BaseUrl.Value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || BaseUrl.Value.Contains('?') || BaseUrl.Value.Contains('#'))) ErrorMessage = UiText.OcrEndpointError;
        if (!Enum.IsDefined(Credentials.Value) || IsWindows && Credentials.Value == CredentialEditKind.Replace
            || Credentials.Value == CredentialEditKind.Replace && (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_secretKey))) ErrorMessage = UiText.OcrCredentialError;
        PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
        return ErrorMessage == null;
    }
    public OcrProviderProfile BuildTarget() => _initial with
    {
        DisplayName = DisplayName.Value, Enabled = Enabled.Value, TimeoutSeconds = int.Parse(TimeoutText.Value, CultureInfo.InvariantCulture),
        BaiduOptions = IsBaidu ? (_initial.BaiduOptions ?? new()) with { Mode = Mode.Value, DetectDirection = DetectDirection.Value, Paragraph = Paragraph.Value,
            LanguageType = LanguageType.Value, BaseUrl = BaseUrl.Value } : _initial.BaiduOptions,
        WindowsOptions = IsWindows ? (_initial.WindowsOptions ?? new()) with { LanguageTag = LanguageTag.Value } : _initial.WindowsOptions
    };
    public ProviderCredentialEdit BuildCredentials() => new(Credentials.Value,
        Credentials.Value == CredentialEditKind.Replace ? new(_apiKey, _secretKey) : null);
    public void Merge(OcrProviderProfile current)
    {
        if (IsDirty || IsNew) return;
        _initial = Clone(current); BaseProfile = Clone(current); InvalidateTest();
        var b = current.BaiduOptions ?? new(); var w = current.WindowsOptions ?? new();
        DisplayName.Merge(current.DisplayName, true); Enabled.Merge(current.Enabled, true); TimeoutText.Merge(current.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), true);
        Mode.Merge(b.Mode, true); DetectDirection.Merge(b.DetectDirection, true); Paragraph.Merge(b.Paragraph, true);
        LanguageType.Merge(b.LanguageType, true); BaseUrl.Merge(b.BaseUrl, true); LanguageTag.Merge(w.LanguageTag, true);
    }
    public void SetTestMessage(string? value) { TestMessage = value; PropertyChanged?.Invoke(this, new(nameof(TestMessage))); }
    private static OcrProviderProfile Clone(OcrProviderProfile p) => p with { BaiduOptions = p.BaiduOptions is { } b ? b with { } : null, WindowsOptions = p.WindowsOptions is { } w ? w with { } : null };
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => "OcrProfileDraftViewModel";
}
