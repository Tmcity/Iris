using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Translation;

/// <summary>
/// 多服务翻译编排器实现。
/// </summary>
internal sealed partial class TranslationOrchestrator : ITranslationOrchestrator
{
    private readonly ITranslationProviderRegistry _registry;
    private readonly ITextPreprocessor _preprocessor;
    private readonly ITargetLanguageResolver _languageResolver;
    private readonly IPromptTemplateProvider _templateProvider;
    private readonly IPromptRenderer _promptRenderer;
    private readonly ITranslationCache _cache;
    private readonly ITranslationHistoryRepository _historyRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TranslationOrchestrator> _logger;

    public TranslationOrchestrator(
        ITranslationProviderRegistry registry,
        ITextPreprocessor preprocessor,
        ITargetLanguageResolver languageResolver,
        IPromptTemplateProvider templateProvider,
        IPromptRenderer promptRenderer,
        ITranslationCache cache,
        ITranslationHistoryRepository historyRepository,
        TimeProvider timeProvider,
        ILogger<TranslationOrchestrator> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));
        _languageResolver = languageResolver ?? throw new ArgumentNullException(nameof(languageResolver));
        _templateProvider = templateProvider ?? throw new ArgumentNullException(nameof(templateProvider));
        _promptRenderer = promptRenderer ?? throw new ArgumentNullException(nameof(promptRenderer));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IAsyncEnumerable<TranslationEvent> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        return TranslateInternalAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<TranslationEvent> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken methodToken,
        [EnumeratorCancellation] CancellationToken enumeratorToken = default)
    {
        // 1. 合成取消令牌，优先检查取消
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(methodToken, enumeratorToken);
        var workCt = linkedCts.Token;

        workCt.ThrowIfCancellationRequested();

        // 2. 捕获请求标量与 ProviderIds 独立只读副本，并进行参数验证
        if (request == null)
        {
            throw new IrisException(ErrorCode.InvalidArgument, "Request cannot be null.");
        }

        if (request.RequestId == Guid.Empty)
        {
            throw new IrisException(ErrorCode.InvalidArgument, "RequestId cannot be empty.");
        }

        if (!Enum.IsDefined(request.Origin))
        {
            throw new IrisException(ErrorCode.InvalidArgument, $"Unknown text origin '{request.Origin}'.");
        }

        List<string>? capturedSelectedIds = null;
        if (request.ProviderIds != null)
        {
            if (request.ProviderIds.Count == 0)
            {
                throw new IrisException(ErrorCode.NotConfigured, "ProviderIds list cannot be empty.");
            }

            var distinct = new List<string>(request.ProviderIds.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in request.ProviderIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new IrisException(ErrorCode.InvalidArgument, "Provider ID cannot be null or whitespace.");
                }

                if (seen.Add(id))
                {
                    distinct.Add(id);
                }
            }

            capturedSelectedIds = distinct;
        }

        // 3. 在第一次异步等待前捕获一次 Cache.Generation
        long? initialGeneration = null;
        bool cacheUsable = true;
        try
        {
            initialGeneration = _cache.Generation;
        }
        catch (Exception ex)
        {
            cacheUsable = false;
            LogCacheAccessFailed(_logger, ex.GetType().Name);
        }

        ITranslationProviderSnapshot? lease = null;
        Task[]? workerTasks = null;
        var isCompletionPhase = false;

        try
        {
            // 4. 借用 Registry 快照，全部执行配置取自该快照
            lease = await _registry.AcquireSnapshotAsync(workCt).ConfigureAwait(false);
            var snapshotSettings = lease.Settings;
            var translationSettings = snapshotSettings.Translation;

            // 预处理文本
            var preparedText = _preprocessor.Prepare(request.Text, request.Origin, translationSettings);
            if (string.IsNullOrWhiteSpace(preparedText))
            {
                throw new IrisException(ErrorCode.InputEmpty, "Prepared text is empty.");
            }

            if (preparedText.Length > translationSettings.MaxInputChars)
            {
                throw new IrisException(
                    ErrorCode.InputTooLong,
                    $"Prepared text length {preparedText.Length} exceeds maximum allowed {translationSettings.MaxInputChars}.");
            }

            // 解析语言
            var resolvedLanguages = _languageResolver.Resolve(
                preparedText,
                request.SourceLanguage,
                request.TargetLanguage,
                translationSettings);

            // 5. Provider 选择与卡片规划
            List<CardPlan> cards;
            if (capturedSelectedIds == null)
            {
                var activeRegistrations = lease.Providers
                    .Where(r => r.Profile.Enabled && r.Provider.Descriptor.IsConfigured)
                    .ToList();

                if (activeRegistrations.Count == 0)
                {
                    throw new IrisException(ErrorCode.NotConfigured, "No configured and enabled translation providers available in snapshot.");
                }

                cards = new List<CardPlan>(activeRegistrations.Count);
                foreach (var reg in activeRegistrations)
                {
                    cards.Add(PrepareCandidateCard(reg, preparedText, resolvedLanguages, translationSettings));
                }
            }
            else
            {
                cards = new List<CardPlan>(capturedSelectedIds.Count);
                foreach (var id in capturedSelectedIds)
                {
                    var reg = lease.Providers.FirstOrDefault(r => string.Equals(r.Profile.Id, id, StringComparison.Ordinal));
                    if (reg != null && reg.Profile.Enabled && reg.Provider.Descriptor.IsConfigured)
                    {
                        cards.Add(PrepareCandidateCard(reg, preparedText, resolvedLanguages, translationSettings));
                    }
                    else if (reg != null)
                    {
                        var descriptor = reg.Provider.Descriptor with { IsConfigured = false };
                        cards.Add(new CardPlan
                        {
                            Descriptor = descriptor,
                            Registration = reg,
                            IsActiveCandidate = false,
                            PreconfiguredError = new ErrorInfo(
                                ErrorCode.NotConfigured,
                                "Provider is not configured or disabled.",
                                Detail: id,
                                ProviderId: id)
                        });
                    }
                    else
                    {
                        var profile = translationSettings.Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                        if (profile != null)
                        {
                            var descriptor = new ProviderDescriptor(profile.Id, profile.DisplayName, profile.Kind.ToString(), profile.Model, false);
                            cards.Add(new CardPlan
                            {
                                Descriptor = descriptor,
                                IsActiveCandidate = false,
                                PreconfiguredError = new ErrorInfo(
                                    ErrorCode.NotConfigured,
                                    "Provider is not configured or disabled.",
                                    Detail: id,
                                    ProviderId: id)
                            });
                        }
                        else
                        {
                            var descriptor = new ProviderDescriptor(id, id, "Unknown", null, false);
                            cards.Add(new CardPlan
                            {
                                Descriptor = descriptor,
                                IsActiveCandidate = false,
                                PreconfiguredError = new ErrorInfo(
                                    ErrorCode.NotConfigured,
                                    "Provider is unknown.",
                                    Detail: id,
                                    ProviderId: id)
                            });
                        }
                    }
                }
            }

            // 6. 卡片列表确定后先 yield TranslationStarted
            var descriptors = cards.Select(c => c.Descriptor).ToList().AsReadOnly();
            yield return new TranslationStarted(request.RequestId, resolvedLanguages, descriptors);

            // 7. 启动有界 Channel、预算跟踪与并发信号量
            var channel = Channel.CreateBounded<TranslationEvent>(
                new BoundedChannelOptions(128)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true
                });

            var budgetTracker = new RequestBudgetTracker();
            var maxConcurrent = Math.Max(1, translationSettings.MaxConcurrentProviders);
            using var concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

            workerTasks = cards.Select(card => ProcessCardAsync(
                card,
                request,
                channel.Writer,
                budgetTracker,
                concurrencySemaphore,
                translationSettings,
                cacheUsable,
                initialGeneration,
                workCt)).ToArray();

            _ = Task.WhenAll(workerTasks).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    channel.Writer.TryComplete(t.Exception);
                }
                else
                {
                    channel.Writer.TryComplete();
                }
            }, TaskScheduler.Default);

            // 8. 消费事件流
            await foreach (var evt in channel.Reader.ReadAllAsync(workCt).ConfigureAwait(false))
            {
                yield return evt;
            }

            // 9. 正常收尾点：所有子任务退出、事件 Channel 正常读尽，最后一次取消检查通过
            workCt.ThrowIfCancellationRequested();

            isCompletionPhase = true;

            // 10.1 尝试写入缓存（仅新鲜、未截断的成功值）
            if (cacheUsable && initialGeneration.HasValue)
            {
                foreach (var card in cards)
                {
                    if (card.IsSucceeded && !card.IsFromCache && !card.IsTruncated && card.CacheKey.HasValue && card.FullText != null)
                    {
                        try
                        {
                            _cache.TrySet(card.CacheKey.Value, card.FullText, initialGeneration.Value);
                        }
                        catch (Exception ex)
                        {
                            LogCacheWriteFailed(_logger, ex.GetType().Name);
                        }
                    }
                }
            }

            // 10.2 尝试保存历史（成功项按卡片顺序，包含缓存命中与截断项）
            var successfulResults = new List<HistoryResult>();
            foreach (var card in cards)
            {
                if (card.IsSucceeded && card.FullText != null)
                {
                    successfulResults.Add(new HistoryResult(
                        card.Descriptor.Id,
                        card.Descriptor.DisplayName,
                        card.Descriptor.Model,
                        card.FullText)
                    {
                        Truncated = card.IsTruncated
                    });
                }
            }

            if (request.RecordHistory &&
                snapshotSettings.History.Enabled &&
                successfulResults.Count > 0)
            {
                var historyEntry = new HistoryEntry
                {
                    CreatedAt = _timeProvider.GetUtcNow(),
                    Origin = request.Origin,
                    SourceText = preparedText,
                    SourceLanguage = resolvedLanguages.Source,
                    TargetLanguage = resolvedLanguages.Target,
                    IsFavorite = false,
                    Results = successfulResults
                };

                try
                {
                    using var historyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
                    await _historyRepository.AddAsync(historyEntry, historyCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogHistorySaveFailed(_logger, ex.GetType().Name);
                }
            }

            // 10.3 释放租约（最迟在 Finished 之前，绝不能早于子任务清理）
            lease?.Dispose();
            lease = null;

            // 10.4 最终 TranslationFinished 事件
            var succeededCount = cards.Count(c => c.IsSucceeded);
            var failedCount = cards.Count(c => !c.IsSucceeded);
            yield return new TranslationFinished(request.RequestId, succeededCount, failedCount);
        }
        finally
        {
            if (!isCompletionPhase)
            {
                try { linkedCts.Cancel(); } catch { }
            }

            if (workerTasks != null && workerTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(workerTasks).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略清理阶段子任务的取消或异常
                }
            }

            lease?.Dispose();
        }
    }

    private CardPlan PrepareCandidateCard(
        TranslationProviderRegistration reg,
        string preparedText,
        ResolvedLanguagePair resolvedLanguages,
        TranslationSettings translationSettings)
    {
        var descriptor = reg.Provider.Descriptor;
        try
        {
            var template = _templateProvider.Get(reg.Profile.PromptTemplateId, translationSettings);
            var protoReq = new ProviderTranslateRequest(preparedText, resolvedLanguages.Source, resolvedLanguages.Target);
            var rendered = _promptRenderer.Render(template, protoReq);
            var providerReq = protoReq with { PreparedPrompt = rendered };
            var cacheKey = TranslationCacheKeyFactory.Create(reg, providerReq);

            return new CardPlan
            {
                Descriptor = descriptor,
                Registration = reg,
                ProviderRequest = providerReq,
                CacheKey = cacheKey,
                IsActiveCandidate = true
            };
        }
        catch (Exception ex)
        {
            var errorInfo = ex is IrisException irisEx
                ? irisEx.ToErrorInfo() with { ProviderId = reg.Profile.Id }
                : new ErrorInfo(
                    ErrorCode.Unknown,
                    "Failed to prepare prompt template.",
                    Detail: ex.GetType().Name,
                    ProviderId: reg.Profile.Id);

            return new CardPlan
            {
                Descriptor = descriptor,
                Registration = reg,
                IsActiveCandidate = false,
                PreconfiguredError = errorInfo
            };
        }
    }

    private async Task ProcessCardAsync(
        CardPlan card,
        TranslationRequest request,
        ChannelWriter<TranslationEvent> writer,
        RequestBudgetTracker budgetTracker,
        SemaphoreSlim concurrencySemaphore,
        TranslationSettings translationSettings,
        bool cacheUsable,
        long? initialGeneration,
        CancellationToken workCt)
    {
        if (workCt.IsCancellationRequested) return;

        // 1. 未配置项或模板准备失败项：无需并发名额与缓存查找，直接输出 Started -> Failed
        if (card.PreconfiguredError != null || !card.IsActiveCandidate)
        {
            var error = card.PreconfiguredError ?? new ErrorInfo(
                ErrorCode.NotConfigured,
                "Provider is not configured.",
                ProviderId: card.Descriptor.Id);

            await writer.WriteAsync(new ProviderStarted(request.RequestId, card.Descriptor.Id), workCt).ConfigureAwait(false);
            await writer.WriteAsync(new ProviderFailed(request.RequestId, card.Descriptor.Id, error, TimeSpan.Zero), workCt).ConfigureAwait(false);
            card.IsCompleted = true;
            card.IsSucceeded = false;
            return;
        }

        // 2. 缓存读取检查（不占并发名额）
        if (translationSettings.CacheEnabled && !request.BypassCache && cacheUsable && card.CacheKey.HasValue)
        {
            string? cachedText = null;
            bool cacheHit = false;
            try
            {
                cacheHit = _cache.TryGet(card.CacheKey.Value, out cachedText);
            }
            catch (Exception ex)
            {
                LogCacheReadFailed(_logger, ex.GetType().Name);
                cacheHit = false;
            }

            if (cacheHit && !string.IsNullOrWhiteSpace(cachedText))
            {
                // 缓存正文计入总预算
                if (cachedText.Length > RequestBudgetTracker.MaxProviderTextLength ||
                    !budgetTracker.TryReserve(cachedText.Length))
                {
                    if (workCt.IsCancellationRequested) return;
                    await writer.WriteAsync(new ProviderStarted(request.RequestId, card.Descriptor.Id), workCt).ConfigureAwait(false);
                    var errorInfo = new ErrorInfo(
                        ErrorCode.InvalidResponse,
                        "翻译结果超过本地长度限制",
                        ProviderId: card.Descriptor.Id);
                    await writer.WriteAsync(new ProviderFailed(request.RequestId, card.Descriptor.Id, errorInfo, TimeSpan.Zero), workCt).ConfigureAwait(false);
                    card.IsCompleted = true;
                    card.IsSucceeded = false;
                    return;
                }

                if (workCt.IsCancellationRequested) return;

                await writer.WriteAsync(new ProviderStarted(request.RequestId, card.Descriptor.Id), workCt).ConfigureAwait(false);
                await writer.WriteAsync(new ProviderCompleted(
                    request.RequestId,
                    card.Descriptor.Id,
                    cachedText,
                    TimeSpan.Zero,
                    FromCache: true,
                    Truncated: false,
                    Usage: null), workCt).ConfigureAwait(false);

                card.IsCompleted = true;
                card.IsSucceeded = true;
                card.IsFromCache = true;
                card.IsTruncated = false;
                card.FullText = cachedText;
                return;
            }
        }

        // 3. 真实 Provider 调用：申请并发名额
        await concurrencySemaphore.WaitAsync(workCt).ConfigureAwait(false);
        try
        {
            if (workCt.IsCancellationRequested) return;

            var startTimestamp = _timeProvider.GetTimestamp();
            await writer.WriteAsync(new ProviderStarted(request.RequestId, card.Descriptor.Id), workCt).ConfigureAwait(false);

            var provider = card.Registration!.Provider;
            var providerReq = card.ProviderRequest!;

            var sb = new StringBuilder();
            int currentTextLength = 0;
            int? latestInputTokens = null;
            int? latestOutputTokens = null;
            TranslationFinishReason? observedFinishReason = null;
            bool finishReasonReceived = false;

            try
            {
                var enumerator = provider.TranslateStreamAsync(providerReq, workCt).GetAsyncEnumerator(workCt);
                try
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        if (workCt.IsCancellationRequested) return;

                        var chunk = enumerator.Current;

                        if (finishReasonReceived)
                        {
                            throw new IrisException(ErrorCode.InvalidResponse, "Chunk received after finish reason.");
                        }

                        if (chunk.Delta == null)
                        {
                            throw new IrisException(ErrorCode.InvalidResponse, "Delta chunk cannot be null.");
                        }

                        if (chunk.Usage != null)
                        {
                            if (chunk.Usage.InputTokens.HasValue) latestInputTokens = chunk.Usage.InputTokens.Value;
                            if (chunk.Usage.OutputTokens.HasValue) latestOutputTokens = chunk.Usage.OutputTokens.Value;
                        }

                        if (chunk.FinishReason.HasValue)
                        {
                            var reason = chunk.FinishReason.Value;
                            if (!Enum.IsDefined(reason))
                            {
                                throw new IrisException(ErrorCode.InvalidResponse, $"Unknown finish reason '{reason}'.");
                            }
                            observedFinishReason = reason;
                            finishReasonReceived = true;
                        }

                        if (chunk.Delta.Length > 0)
                        {
                            if (currentTextLength + chunk.Delta.Length > RequestBudgetTracker.MaxProviderTextLength)
                            {
                                throw new IrisException(ErrorCode.InvalidResponse, "翻译结果超过本地长度限制");
                            }

                            if (!budgetTracker.TryReserve(chunk.Delta.Length))
                            {
                                throw new IrisException(ErrorCode.InvalidResponse, "翻译结果超过本地长度限制");
                            }

                            currentTextLength += chunk.Delta.Length;
                            sb.Append(chunk.Delta);

                            foreach (var slice in SplitDelta(chunk.Delta))
                            {
                                if (workCt.IsCancellationRequested) return;
                                await writer.WriteAsync(new ProviderDelta(request.RequestId, card.Descriptor.Id, slice), workCt).ConfigureAwait(false);
                            }
                        }
                    }
                }
                finally
                {
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogCleanupFailed(_logger, card.Descriptor.Id, ex.GetType().Name);
                    }
                }

                if (workCt.IsCancellationRequested) return;

                if (!finishReasonReceived || !observedFinishReason.HasValue)
                {
                    throw new IrisException(ErrorCode.InvalidResponse, "Provider stream ended without finish reason.");
                }

                var fullText = sb.ToString();
                if (string.IsNullOrWhiteSpace(fullText))
                {
                    throw new IrisException(ErrorCode.InvalidResponse, "Provider returned empty translation result.");
                }

                var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
                var isTruncated = observedFinishReason == TranslationFinishReason.Truncated;
                TokenUsage? usage = (latestInputTokens != null || latestOutputTokens != null)
                    ? new TokenUsage(latestInputTokens, latestOutputTokens)
                    : null;

                var completedEvent = new ProviderCompleted(
                    request.RequestId,
                    card.Descriptor.Id,
                    fullText,
                    elapsed,
                    FromCache: false,
                    Truncated: isTruncated,
                    Usage: usage);

                await writer.WriteAsync(completedEvent, workCt).ConfigureAwait(false);

                card.IsCompleted = true;
                card.IsSucceeded = true;
                card.IsFromCache = false;
                card.IsTruncated = isTruncated;
                card.FullText = fullText;
            }
            catch (OperationCanceledException) when (workCt.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (workCt.IsCancellationRequested) return;

                var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
                ErrorInfo errorInfo;
                if (ex is IrisException irisEx)
                {
                    errorInfo = irisEx.ToErrorInfo() with { ProviderId = card.Descriptor.Id };
                }
                else
                {
                    errorInfo = new ErrorInfo(
                        ErrorCode.Unknown,
                        "Translation provider execution failed.",
                        Detail: ex.GetType().Name,
                        ProviderId: card.Descriptor.Id);
                }

                LogProviderFailed(_logger, request.RequestId, card.Descriptor.Id, errorInfo.Code.ToString(), ex.GetType().Name);

                var failedEvent = new ProviderFailed(request.RequestId, card.Descriptor.Id, errorInfo, elapsed);
                await writer.WriteAsync(failedEvent, workCt).ConfigureAwait(false);

                card.IsCompleted = true;
                card.IsSucceeded = false;
            }
        }
        finally
        {
            concurrencySemaphore.Release();
        }
    }

    private static IEnumerable<string> SplitDelta(string delta)
    {
        if (delta.Length <= RequestBudgetTracker.MaxDeltaLength)
        {
            yield return delta;
            yield break;
        }

        int offset = 0;
        while (offset < delta.Length)
        {
            int remaining = delta.Length - offset;
            if (remaining <= RequestBudgetTracker.MaxDeltaLength)
            {
                yield return delta.Substring(offset, remaining);
                break;
            }

            int sliceLength = RequestBudgetTracker.MaxDeltaLength;
            if (char.IsHighSurrogate(delta[offset + sliceLength - 1]))
            {
                sliceLength--;
            }

            yield return delta.Substring(offset, sliceLength);
            offset += sliceLength;
        }
    }

    private sealed class CardPlan
    {
        public required ProviderDescriptor Descriptor { get; init; }
        public TranslationProviderRegistration? Registration { get; init; }
        public ProviderTranslateRequest? ProviderRequest { get; init; }
        public TranslationCacheKey? CacheKey { get; init; }
        public bool IsActiveCandidate { get; init; }
        public ErrorInfo? PreconfiguredError { get; init; }

        public volatile bool IsCompleted;
        public volatile bool IsSucceeded;
        public volatile bool IsFromCache;
        public volatile bool IsTruncated;
        public string? FullText;
    }

    private sealed class RequestBudgetTracker
    {
        public const int MaxDeltaLength = 4096;
        public const int MaxProviderTextLength = 1_048_576;
        public const long MaxTotalTextLength = 4_194_304;

        private long _totalLengthReserved;

        public bool TryReserve(int length)
        {
            if (length <= 0) return true;

            while (true)
            {
                var current = Interlocked.Read(ref _totalLengthReserved);
                var next = current + length;
                if (next > MaxTotalTextLength)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _totalLengthReserved, next, current) == current)
                {
                    return true;
                }
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Translation cache generation access failed: {ExceptionType}.")]
    private static partial void LogCacheAccessFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Translation cache read failed: {ExceptionType}.")]
    private static partial void LogCacheReadFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Translation cache write failed: {ExceptionType}.")]
    private static partial void LogCacheWriteFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Translation history save failed: {ExceptionType}.")]
    private static partial void LogHistorySaveFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Provider {ProviderId} failed for request {RequestId} with code {ErrorCode}: {ExceptionType}.")]
    private static partial void LogProviderFailed(ILogger logger, Guid requestId, string providerId, string errorCode, string exceptionType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Provider {ProviderId} enumerator disposal failed: {ExceptionType}.")]
    private static partial void LogCleanupFailed(ILogger logger, string providerId, string exceptionType);
}
