using Iris.Core.Common;
using Iris.Core.Settings;

namespace Iris.Core.Translation;

/// <summary>
/// 翻译服务（一个 Profile 对应一个实例）。实现约束：
/// - 必须线程安全、可并发调用；不持有 UI 引用；不访问 ISecretStore（凭据由构造时注入）。
/// - 只产出“译文正文”：忽略 reasoning/thinking 内容，按配置剥离 &lt;think&gt;…&lt;/think&gt;。
/// - 已知失败抛 IrisException（带 ProviderId）；cancellationToken 取消时抛 OperationCanceledException。
/// - 超时（Profile.TimeoutSeconds）由 Provider 内部用链接的 CTS 实现，并转换为 IrisException(Timeout)。
/// - 拒答 → IrisException(ContentRefused)；截断 → 最后一个 Chunk 的 FinishReason = Truncated（不抛异常）。
/// </summary>
public interface ITranslationProvider
{
    ProviderDescriptor Descriptor { get; }

    IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(ProviderTranslateRequest request, CancellationToken cancellationToken);

    /// <summary>用固定短句（"Hello, world." → PrimaryTargetLanguage）做一次真实调用；不抛异常，结果写入返回值。</summary>
    Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken);
}

/// <summary>可选能力：列出可用模型（设置页下拉框）。OpenAI 兼容：GET {BaseUrl}/models。</summary>
public interface IModelCatalogSource
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);
}

public interface ITranslationProviderFactory
{
    TranslationProviderKind Kind { get; }

    ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials);
}

/// <summary>
/// 根据 Settings + SecretStore 构建并缓存 Provider 实例；监听 ISettingsService.Changed 与 ISecretStore.Changed
/// 自动重建，重建完成后触发 Changed。
/// </summary>
public interface ITranslationProviderRegistry
{
    /// <summary>Enabled 的 Profile 对应实例，按 Order 升序（含未配置的，调用方按 Descriptor.IsConfigured 判断）。</summary>
    IReadOnlyList<ITranslationProvider> GetEnabled();

    ITranslationProvider? Find(string providerId);

    /// <summary>设置页“测试连接 / 获取模型”用：以草稿 Profile 与草稿凭据临时创建，不缓存。</summary>
    ITranslationProvider CreateTransient(TranslationProviderProfile profile, ProviderCredentials credentials);

    event EventHandler? Changed;
}
