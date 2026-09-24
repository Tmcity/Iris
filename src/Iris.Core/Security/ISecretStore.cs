namespace Iris.Core.Security;

/// <summary>
/// 密钥存储（Platform.Windows 实现：DPAPI CurrentUser + 固定 entropy，文件 secrets.dat）。
/// 解密失败（换机器 / 换用户）时 Get 返回 null 并记录警告，不抛异常。线程安全。
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
    public static string ApiKey(string providerId) => $"provider/{providerId}/api-key";

    public static string SecretKey(string providerId) => $"provider/{providerId}/secret-key";
}
