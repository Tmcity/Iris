using Iris.Core.Settings;

namespace Iris.Core.Translation;

/// <summary>
/// 翻译编排器（Core 内实现）。TranslateAsync 语义：
/// 1) 首次枚举借用 Registry 快照；预处理、语言、模板与执行选项全部使用 Snapshot.Settings。
///    预处理后空文本抛 IrisException(InputEmpty)；超过 MaxInputChars 抛 InputTooLong。
/// 2) ProviderIds 为 null 时选择快照中 IsConfigured 的 Provider；空集合或没有可用项时抛 NotConfigured。
///    显式指定但不存在/未配置的项产出对应 ProviderFailed(NotConfigured)。
/// 3) 在排队前准备每项 PreparedPrompt、实际请求缓存键与缓存 Generation；后续不读取动态设置重算。
/// 4) BypassCache=false 且 CacheEnabled 时查缓存；命中产出 ProviderStarted、ProviderCompleted(FromCache=true)。
///    否则调用流式接口，并发度不超过快照中的 MaxConcurrentProviders，事件由有界 Channel 合并。
/// 5) 单个 Provider 失败不影响其它项，转为 ProviderFailed；失败/取消/截断不写缓存。
/// 6) 全部子任务结束后，未截断成功值以原 Generation 尝试写缓存；BypassCache 只跳过读取。
///    RecordHistory 且快照 History.Enabled 且至少一个成功时写历史，写入失败只记安全日志。
/// 7) 调用取消或消费者提前结束时取消并等待全部子任务退出，再释放租约；调用取消抛 OperationCanceledException，不写历史。
/// 请求级错误在首次 MoveNextAsync 抛出。精确选择、事件与资源边界见 TRANSLATION_EXECUTION.md 和 T-013 工单。
/// </summary>
public interface ITranslationOrchestrator
{
    IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 文本预处理（规则详见 ARCH §7.5）：换行统一为 \n；移除 U+200B / U+FEFF / U+2060（保留 ZWJ 与 ZWNJ）；U+00A0 → 空格；
/// 连续多个空行压缩为 1 个；Origin=Selection 且 SelectionLineBreaks=MergeSmart 时执行软换行合并；最后去除首尾空白。
/// </summary>
public interface ITextPreprocessor
{
    string Prepare(string raw, TextOrigin origin);

    /// <summary>使用指定配置执行同一预处理规则，不读取当前设置、不修改传入配置。</summary>
    string Prepare(string raw, TextOrigin origin, TranslationSettings settings);
}
