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

/// <summary>OCR 注册表原型；T-030 落地前按 PROVIDER_REGISTRY.md 同步租约与发布屏障接口，当前不实现此原型。</summary>
public interface IOcrProviderRegistry
{
    IOcrProvider? GetActive();

    IOcrProvider? GetFallback();

    IOcrProvider? Find(string providerId);

    IReadOnlyList<IOcrProvider> GetAll();

    IOcrProvider CreateTransient(OcrProviderProfile profile, ProviderCredentials credentials);

    event EventHandler? Changed;
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
