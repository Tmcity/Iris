using System.ComponentModel;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Settings;

namespace Iris.App.Settings;

internal sealed record NetworkOption<T>(T Value, string Name);

internal sealed class NetworkSettingsDraftViewModel : INotifyPropertyChanged
{
    private readonly Func<bool> _canEdit;
    private readonly Action _changed;
    private NetworkSettings _latest;
    private string? _baseUrl, _baseSet;
    private string _password = "";
    private bool _updating;
    public DraftValue<ProxyMode> Mode { get; }
    public DraftValue<bool> BypassLocal { get; }
    public DraftValue<string> Url { get; }
    public DraftValue<CredentialEditKind> Credentials { get; }
    public DraftValue<string> UserName { get; }
    public string ReplacementPassword => _password;
    public string? ErrorMessage { get; private set; }
    public bool IsReplacing => Credentials.Value == CredentialEditKind.Replace;
    public bool IsDirty => Mode.IsDirty || BypassLocal.IsDirty || Url.IsDirty || Credentials.Value != CredentialEditKind.Keep;
    public long Revision { get; private set; }
    public static IReadOnlyList<NetworkOption<ProxyMode>> Modes { get; } =
        [new(ProxyMode.System, UiText.NetworkSystem), new(ProxyMode.None, UiText.NetworkNone), new(ProxyMode.Custom, UiText.NetworkCustom)];
    public static IReadOnlyList<NetworkOption<CredentialEditKind>> CredentialKinds { get; } =
        [new(CredentialEditKind.Keep, UiText.NetworkKeep), new(CredentialEditKind.Replace, UiText.NetworkReplace), new(CredentialEditKind.Clear, UiText.NetworkClear)];

    internal NetworkSettingsDraftViewModel(NetworkSettings current, Func<bool> canEdit, Action changed)
    {
        _canEdit = canEdit; _changed = changed; _latest = current; _baseUrl = current.CustomProxyUrl; _baseSet = current.ProxyCredentialSetId;
        Mode = new(current.ProxyMode, canEdit, Changed);
        BypassLocal = new(current.BypassProxyOnLocal, canEdit, Changed);
        Url = new(current.CustomProxyUrl ?? "", canEdit, UrlChanged);
        Credentials = new(CredentialEditKind.Keep, canEdit, CredentialChanged);
        UserName = new("", () => canEdit() && IsReplacing, Changed);
    }
    private void Changed()
    {
        if (_updating) return;
        Revision++; ErrorMessage = null;
        PropertyChanged?.Invoke(this, new(nameof(IsDirty))); PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
        _changed();
    }
    private void UrlChanged()
    {
        if (_updating) return;
        if (NetworkProxyCodec.HasUserInfo(Url.Value))
        {
            try
            {
                var uri = NetworkProxyCodec.ParseUri(Url.Value, allowUserInfo: true);
                var credentials = NetworkProxyCodec.ReadUserInfo(Url.Value);
                _updating = true;
                Credentials.Value = CredentialEditKind.Replace; UserName.Value = credentials.UserName;
                _password = credentials.Password; Url.Value = uri.AbsoluteUri;
                PropertyChanged?.Invoke(this, new(nameof(ReplacementPassword)));
                PropertyChanged?.Invoke(this, new(nameof(IsReplacing)));
            }
            catch (IrisException) { /* Preserve invalid current text for correction; capture will reject it safely. */ }
            finally { _updating = false; }
        }
        Changed();
    }
    private void CredentialChanged()
    {
        if (_updating) return;
        if (!IsReplacing) ClearReplacement();
        if (Credentials.Value == CredentialEditKind.Keep && !Url.IsDirty) Merge(_latest);
        PropertyChanged?.Invoke(this, new(nameof(IsReplacing)));
        Changed();
    }
    public void SetPassword(string value)
    {
        if (!_canEdit() || !IsReplacing || _password == value) return;
        _password = value; PropertyChanged?.Invoke(this, new(nameof(ReplacementPassword))); Changed();
    }
    public void ClearReplacement()
    {
        _password = ""; UserName.Merge("", reset: true);
        PropertyChanged?.Invoke(this, new(nameof(ReplacementPassword)));
    }
    public bool TryCapture(out NetworkSettingsPatch? patch, bool forTest = false)
    {
        patch = null; ErrorMessage = null;
        try
        {
            if (!IsDirty && !forTest) return true;
            if (!Enum.IsDefined(Mode.Value) || !Enum.IsDefined(Credentials.Value)) throw NetworkProxyCodec.Invalid();
            if (Url.IsDirty || Credentials.Value != CredentialEditKind.Keep || Mode.Value == ProxyMode.Custom)
            {
                var uri = NetworkProxyCodec.ParseUri(Url.Value);
                if (IsReplacing) NetworkProxyCodec.ValidateCredentials(new(UserName.Value, _password), uri);
                if (Url.IsDirty && uri.AbsoluteUri != _baseUrl && Credentials.Value == CredentialEditKind.Keep)
                { ErrorMessage = UiText.NetworkAddressNeedsCredentials; return false; }
                if (Url.IsDirty) Url.Value = uri.AbsoluteUri;
            }
            if (IsDirty) patch = new(Mode.Edit(), Url.IsDirty ? new(_baseUrl, Url.Value) : null, BypassLocal.Edit(), CaptureCredentials());
            return true;
        }
        catch (IrisException) { ErrorMessage = UiText.NetworkInvalidInput; return false; }
        finally { PropertyChanged?.Invoke(this, new(nameof(ErrorMessage))); }
    }
    private ProxyCredentialEdit? CaptureCredentials() => Credentials.Value == CredentialEditKind.Keep ? null
        : new(Credentials.Value, _baseUrl, _baseSet, IsReplacing ? new(UserName.Value, _password) : null);
    public ProxyDraft CaptureDraft() => new(
        new() { ProxyMode = Mode.Base, BypassProxyOnLocal = BypassLocal.Base, CustomProxyUrl = _baseUrl, ProxyCredentialSetId = _baseSet },
        new() { ProxyMode = Mode.Value, BypassProxyOnLocal = BypassLocal.Value, CustomProxyUrl = Url.IsDirty ? Url.Value : _baseUrl, ProxyCredentialSetId = _baseSet },
        CaptureCredentials());
    public void Merge(NetworkSettings current, bool reset = false)
    {
        _latest = current;
        bool preserveAddressIntent = !reset && (Url.IsDirty || Credentials.Value != CredentialEditKind.Keep);
        _updating = true;
        try
        {
            Mode.Merge(current.ProxyMode, reset); BypassLocal.Merge(current.BypassProxyOnLocal, reset);
            if (!preserveAddressIntent) { _baseUrl = current.CustomProxyUrl; _baseSet = current.ProxyCredentialSetId; Url.Merge(current.CustomProxyUrl ?? "", reset); }
            if (reset) { Credentials.Merge(CredentialEditKind.Keep, true); ClearReplacement(); ErrorMessage = null; Revision++; }
        }
        finally { _updating = false; }
        PropertyChanged?.Invoke(this, new(nameof(IsReplacing))); PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
        PropertyChanged?.Invoke(this, new(nameof(IsDirty)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => "NetworkSettingsDraft";
}

internal sealed record NetworkEndpointChoice(NetworkEndpointSelection Selection, string Name, string? Address, bool OfficialDefault, int ProviderKind)
{
    public bool IsValid => OfficialDefault || !string.IsNullOrWhiteSpace(Address) && Address == Address.Trim() && !Address.Any(char.IsControl)
        && Uri.TryCreate(Address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
        && !NetworkProxyCodec.HasUserInfo(Address) && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    public string EndpointText => OfficialDefault ? UiText.NetworkOfficialEndpoint : IsValid ? Address! : UiText.NetworkInvalidEndpoint;
    public bool SameEndpoint(NetworkEndpointChoice? other) => other != null && Selection == other.Selection && Address == other.Address
        && OfficialDefault == other.OfficialDefault && ProviderKind == other.ProviderKind;
    internal static NetworkEndpointChoice[] FromSettings(AppSettings settings) => settings.Translation.Providers
        .Where(p => p.Kind is TranslationProviderKind.OpenAICompatible or TranslationProviderKind.Anthropic)
        .Select(p => new NetworkEndpointChoice(new(NetworkEndpointKind.Translation, p.Id), UiText.NetworkTranslationEndpoint + p.DisplayName,
            p.BaseUrl, p.Kind == TranslationProviderKind.Anthropic && string.IsNullOrWhiteSpace(p.BaseUrl), (int)p.Kind))
        .Concat(settings.Ocr.Providers.Where(p => p.Kind == OcrProviderKind.BaiduCloud)
            .Select(p => new NetworkEndpointChoice(new(NetworkEndpointKind.Ocr, p.Id), UiText.NetworkOcrEndpoint + p.DisplayName,
                (p.BaiduOptions ?? new()).BaseUrl, false, (int)p.Kind))).ToArray();
    public override string ToString() => "NetworkEndpointChoice";
}
