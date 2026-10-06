using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Settings;

namespace Iris.Core.Ocr;

/// <summary>
/// OCR 服务。实现约束同 ITranslationProvider（线程安全、凭据注入、IrisException、超时内部处理）。
/// 调用方保证传入图像已满足 Constraints（由 IOcrService 调用 IImagePreprocessor 完成）。
/// 识别成功但无文字时返回 Lines 为空的结果（不抛异常）。
/// 百度实现的Token代次、有限重试、表单总预算和完整收尾见 BAIDU_OCR.md；调用期间输入图像字节只读。
/// WindowsLocal实现的能力/语言选择、rune单空格处理和位图完整收尾见 WINDOWS_OCR.md，不将选中语言伪称检测结果。
/// </summary>
public interface IOcrProvider
{
    ProviderDescriptor Descriptor { get; }

    ImageConstraints Constraints { get; }

    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken);

    /// <summary>百度：仅鉴权获取 access_token（不消耗识别额度，不仅凭调用前缓存报告成功）；Windows：检查语言包是否可用。</summary>
    Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken);
}

public interface IOcrProviderFactory
{
    OcrProviderKind Kind { get; }

    IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials);
}

/// <summary>OCR注册表。异步快照、发布屏障与失败清理语义见OCR_PROVIDER_REGISTRY.md。
/// 所有Provider只可在快照租约内使用；不得自行处置借出的Provider。</summary>
public interface IOcrProviderRegistry : IAsyncDisposable
{
    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<IOcrProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IOcrProviderSnapshot> CreateTransientAsync(
        OcrProviderProfile profile, ProviderCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>立即封闭新准入/订阅；不等待外部租约，不处置仍在借用的Provider。</summary>
    void StopAccepting();

    /// <summary>停止并等待构造/刷新/全部租约及真实清理；失败保有原资源，后续调用可重试。
    /// cancellationToken仅取消本等待者，不撤销注册表拥有的停止任务。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>重试已退休且无租约的原实例清理，不关闭正常准入或处置当前代。</summary>
    Task RetryCleanupAsync(CancellationToken cancellationToken = default);

    event EventHandler? Changed;
}

/// <summary>固定的OCR实例代次。禁止记录整份Profile/对象；InstanceId不由凭据派生。</summary>
public sealed record OcrProviderRegistration(
    OcrProviderProfile Profile, IOcrProvider Provider, Guid InstanceId);

/// <summary>稳定的设置与按配置列表顺序排列的已启用OCR注册项。
/// 首次释放立即拒绝属性访问；异步释放等待本租约引发的实际清理，失败可重复释放重试。
/// 消费者必须在所有Provider子任务退出后释放；注册表停止后仍可安全释放。</summary>
public interface IOcrProviderSnapshot : IAsyncDisposable
{
    AppSettings Settings { get; }
    IReadOnlyList<OcrProviderRegistration> Providers { get; }
}

/// <summary>
/// OCR 编排（Core 内实现）：选 Provider（providerId ?? Active）→ IImagePreprocessor → RecognizeAsync →
/// IOcrTextNormalizer。主服务未配置或抛出 NotConfigured / NetworkUnavailable / Timeout / ProviderUnavailable / QuotaExceeded
/// 时，若存在 Fallback 则自动改用 Fallback（UsedFallback=true）。最终无文字 → IrisException(OcrNoTextFound)。
/// </summary>
public interface IOcrService
{
    Task<OcrOutcome> RecognizeAsync(ImageData image, string? providerId = null, CancellationToken cancellationToken = default);
}

public interface IOcrTextNormalizer
{
    string Normalize(IReadOnlyList<OcrLine> lines, OcrTextLayout layout);
}
