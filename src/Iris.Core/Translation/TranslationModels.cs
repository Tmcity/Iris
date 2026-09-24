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

    /// <summary>null = 所有已启用且已配置的服务；非空 = 仅这些服务（用于单卡片重试）。</summary>
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
public sealed record ProviderTranslateRequest(string Text, string SourceLanguage, string TargetLanguage);

// ---------------- 翻译会话事件流 ----------------
// 顺序保证：TranslationStarted 一定最先；每个 Provider 依次为 ProviderStarted → ProviderDelta* →
// (ProviderCompleted | ProviderFailed)；TranslationFinished 一定最后。不同 Provider 的事件相互交错。

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
