namespace Iris.Core.Translation;

/// <summary>
/// 翻译编排器（Core 内实现）。TranslateAsync 语义：
/// 1) ITextPreprocessor 预处理；空文本抛 IrisException(InputEmpty)；超过 MaxInputChars 抛 InputTooLong。
/// 2) ITargetLanguageResolver 解析语言对。
/// 3) 选取 Provider：ProviderIds 为 null 取 Registry.GetEnabled() 中 IsConfigured 的；为空集合时抛 NotConfigured。
///    ProviderIds 指定但不存在/未配置的 → 对应 ProviderFailed(NotConfigured)。
/// 4) 每个 Provider：先查缓存（BypassCache=false 且 CacheEnabled）→ 命中直接 ProviderCompleted(FromCache=true)；
///    否则调用流式接口，并发度 ≤ MaxConcurrentProviders，事件经 Channel 合并后按到达顺序产出。
/// 5) 单个 Provider 失败不影响其他 Provider；异常统一转为 ProviderFailed。
/// 6) 全部结束后：成功结果写缓存；RecordHistory 且 History.Enabled 且至少一个成功 → 写历史（失败只记日志）。
/// 7) cancellationToken 取消：停止所有 Provider，枚举抛 OperationCanceledException，不写历史。
/// 请求级错误（第 1、3 步）在第一次 MoveNextAsync 时以 IrisException 抛出。
/// </summary>
public interface ITranslationOrchestrator
{
    IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 文本预处理：去首尾空白；换行统一为 \n；移除零宽字符（U+200B/200C/200D/FEFF）；连续 3 个以上空行压缩为 1 个；
/// Origin=Selection 且 SelectionLineBreaks=MergeSmart 时合并段内硬换行（规则同 IOcrTextNormalizer.MergeParagraphs）。
/// </summary>
public interface ITextPreprocessor
{
    string Prepare(string raw, TextOrigin origin);
}
