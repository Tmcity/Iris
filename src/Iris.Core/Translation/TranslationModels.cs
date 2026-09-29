using Iris.Core.Common;
using Iris.Core.Languages;

namespace Iris.Core.Translation;

/// <summary>文本来源。写入历史，并影响预处理策略。</summary>
public enum TextOrigin
{
    Input,
    Selection,
    Screenshot,
    Clipboard,
    FloatingBall,
    CommandLine,
    History,
}

/// <summary>一次翻译会话的请求。</summary>
public sealed record TranslationRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public required string Text { get; init; }
    public string SourceLanguage { get; init; } = LanguageCodes.Auto;

    /// <summary>null = 由 ITargetLanguageResolver 自动决定。</summary>
    public string? TargetLanguage { get; init; }

    /// <summary>null = 所有已启用且已配置项；空集合抛 NotConfigured；非空列表按序数精确去重并保持首次出现顺序。
    /// 不存在/禁用/未配置项生成失败卡片；首次枚举复制列表，之后外部修改不影响请求。</summary>
    public IReadOnlyList<string>? ProviderIds { get; init; }

    public TextOrigin Origin { get; init; } = TextOrigin.Input;
    public bool BypassCache { get; init; }
    public bool RecordHistory { get; init; } = true;
}

public sealed record TokenUsage(int? InputTokens, int? OutputTokens);

public enum TranslationFinishReason
{
    Completed,
    Truncated,
}

/// <summary>Provider 流式输出的一个片段。最后一个片段可只携带 Usage / FinishReason（Delta 为空串）。</summary>
public readonly record struct TranslationChunk(string Delta, TokenUsage? Usage = null, TranslationFinishReason? FinishReason = null);

/// <summary>发给单个 Provider 的请求（语言已解析、文本已预处理）。</summary>
public sealed record ProviderTranslateRequest(string Text, string SourceLanguage, string TargetLanguage)
{
    /// <summary>非 null 时 Provider 直接发送此已渲染值，不再查找/渲染模板；null 保留直接调用行为。
    /// 编排器从同一注册表快照准备此值，并使用同一请求计算缓存键。不得记录整个请求。</summary>
    public RenderedPrompt? PreparedPrompt { get; init; }
}

// ---------------- 翻译会话事件流 ----------------
// 正常顺序：TranslationStarted 唯一最先；每项 ProviderStarted → ProviderDelta* →
// (ProviderCompleted | ProviderFailed)，TranslationFinished 唯一最后。不同 Provider 事件可交错。
// 请求级错误、正常收尾前取消及消费者提前结束不产生 TranslationFinished。

public abstract record TranslationEvent(Guid RequestId);

/// <param name="Providers">本次参与的 Provider（UI 据此预先创建结果卡片，顺序即卡片顺序）。</param>
public sealed record TranslationStarted(Guid RequestId, ResolvedLanguagePair Languages, IReadOnlyList<ProviderDescriptor> Providers)
    : TranslationEvent(RequestId);

public sealed record ProviderStarted(Guid RequestId, string ProviderId) : TranslationEvent(RequestId);

public sealed record ProviderDelta(Guid RequestId, string ProviderId, string Delta) : TranslationEvent(RequestId);

/// <param name="Text">完整译文（权威值，UI 收到后用它覆盖累积的 Delta）。</param>
/// <param name="Truncated">因 max_tokens 截断，UI 需提示“结果不完整”。</param>
public sealed record ProviderCompleted(
    Guid RequestId,
    string ProviderId,
    string Text,
    TimeSpan Elapsed,
    bool FromCache,
    bool Truncated,
    TokenUsage? Usage) : TranslationEvent(RequestId);

public sealed record ProviderFailed(Guid RequestId, string ProviderId, Iris.Core.Errors.ErrorInfo Error, TimeSpan Elapsed)
    : TranslationEvent(RequestId);

public sealed record TranslationFinished(Guid RequestId, int SucceededCount, int FailedCount) : TranslationEvent(RequestId);
