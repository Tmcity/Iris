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

/// <summary>按设置和凭据管理 Provider 实例。构造函数不加载设置、不创建实例；首次刷新在设置加载后进行。
/// 退休实例在最后一个快照释放后清理；Dispose 后拒绝新操作，已有快照仍可安全释放。</summary>
public interface ITranslationProviderRegistry : IProviderConfigurationObserver, IDisposable
{
    /// <summary>必要时刷新并原子获取快照。按 Order 稳定排序，包含已启用但未配置的项。</summary>
    Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>以独立捕获的草稿及显式凭据创建单项快照；忽略 Enabled，不缓存、不写盘、不触发 Changed。</summary>
    Task<ITranslationProviderSnapshot> CreateTransientAsync(
        TranslationProviderProfile profile,
        ProviderCredentials credentials,
        CancellationToken cancellationToken = default);

    /// <summary>有效注册项、顺序、Descriptor 或实例代次变化后的通知；锁外触发，可合并，订阅方通过新快照读取状态。</summary>
    event EventHandler? Changed;
}
