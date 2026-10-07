// CCR-036: proxy settings and managed network snapshots.
namespace Iris.Core.Settings;

public sealed record ProxyCredentials(string UserName, string Password)
{
    public override string ToString() => "ProxyCredentials";
}

// Base 地址/引用同时绑定凭据意图。Kind 使用既有 CredentialEditKind。
// Replace 必须提供非 null 的完整对（空字符串是显式空值）；Keep/Clear 的 Replacement 必须 null。
public sealed record ProxyCredentialEdit(
    CredentialEditKind Kind,
    string? BaseProxyUrl,
    string? BaseCredentialSetId,
    ProxyCredentials? Replacement = null)
{
    public override string ToString() => "ProxyCredentialEdit";
}

public sealed record NetworkSettingsPatch(
    SettingsFieldEdit<ProxyMode>? ProxyMode = null,
    SettingsFieldEdit<string?>? CustomProxyUrl = null,
    SettingsFieldEdit<bool>? BypassProxyOnLocal = null,
    ProxyCredentialEdit? Credentials = null)
{
    public override string ToString() => "NetworkSettingsPatch";
}
