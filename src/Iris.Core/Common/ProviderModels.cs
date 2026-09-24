using Iris.Core.Errors;

namespace Iris.Core.Common;

/// <summary>Provider 的运行时描述（给 UI 展示与日志使用）。</summary>
/// <param name="Id">对应 Profile.Id。</param>
/// <param name="KindName">Kind 枚举名，如 "OpenAICompatible" / "BaiduCloud"。</param>
/// <param name="IsConfigured">必需凭据是否齐全（例如 Key 已填写）。</param>
public sealed record ProviderDescriptor(string Id, string DisplayName, string KindName, string? Model, bool IsConfigured);

/// <summary>Provider 凭据。由 Registry 从 ISecretStore 读取后注入；Provider 本身不访问 ISecretStore。</summary>
public sealed record ProviderCredentials(string? ApiKey, string? SecretKey = null)
{
    public static ProviderCredentials None { get; } = new(ApiKey: null);

    /// <summary>日志安全：永不输出密钥明文。</summary>
    public override string ToString() => $"ProviderCredentials {{ ApiKey = {(ApiKey is null ? "<null>" : "***")}, SecretKey = {(SecretKey is null ? "<null>" : "***")} }}";
}

/// <summary>“测试连接”结果。</summary>
/// <param name="Sample">翻译服务：测试译文；OCR：可为空。</param>
public sealed record ProviderTestResult(bool Success, TimeSpan Latency, string? Sample = null, ErrorInfo? Error = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
