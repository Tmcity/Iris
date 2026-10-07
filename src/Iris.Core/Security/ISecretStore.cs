namespace Iris.Core.Security;

/// <summary>
/// 密钥存储（Platform.Windows 实现：DPAPI CurrentUser + 固定 entropy，文件 secrets.dat，格式与细节见 ARCH §12.3）。线程安全。
/// - Get：键不存在，或解密失败（换机器 / 换用户 / 文件被篡改）→ 返回 null，不抛异常（解密失败每个键只记一次警告）。
/// - Set：value 为 null 抛 ArgumentNullException；空字符串等价于 Remove。同步原子写盘，失败抛 IrisException(StorageFailure)
///   且内存状态不变；文件无法读取（只读降级模式）且重新加载仍失败时抛 IrisException(SecretUnavailable)。
/// - Remove：键不存在返回 false（不写盘、不触发事件）；其余同 Set。
/// - Changed：写盘成功后在内部锁之外触发，参数为发生变化的 key；可能在任意线程触发。
/// Set / Remove 会同步写盘，调用方应在后台线程调用。
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    bool Remove(string key);

    /// <summary>参数为发生变化的 key。</summary>
    event EventHandler<string>? Changed;
}

public static class SecretKeys
{
    public const string LegacyProxyCredentials = "network/proxy-credentials";
    public static string ProxyCredentialSet(string credentialSetId) => $"network/proxy-credentials/{credentialSetId}";
    public static string ApiKey(string providerId) => $"provider/{providerId}/api-key";

    public static string SecretKey(string providerId) => $"provider/{providerId}/secret-key";

    public static string CredentialSet(string credentialSetId) => $"credential-set/{credentialSetId}";
}
