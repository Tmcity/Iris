using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models;
using Anthropic.Models.Beta;
using Anthropic.Models.Messages;
using Iris.Core.Common;
using Iris.Core.Diagnostics;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Microsoft.Extensions.Logging;
using Beta = Anthropic.Models.Beta.Messages;

namespace Iris.Providers.Translation.Anthropic;

internal sealed partial class AnthropicTranslationProvider : ITranslationProvider, IModelCatalogSource, IDisposable
{
    private readonly AsyncLocal<Action?> _currentResetTimeout = new();
    private readonly TranslationProviderProfile _profile;
    private readonly AnthropicOptions _options;
    private readonly ProviderCredentials _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPromptTemplateProvider _promptTemplates;
    private readonly IPromptRenderer _promptRenderer;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnthropicTranslationProvider> _logger;
    private readonly AnthropicClient _client;

    public AnthropicTranslationProvider(
        TranslationProviderProfile profile,
        ProviderCredentials credentials,
        IHttpClientFactory httpClientFactory,
        IPromptTemplateProvider promptTemplates,
        IPromptRenderer promptRenderer,
        ISettingsService settings,
        TimeProvider timeProvider,
        ILogger<AnthropicTranslationProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(promptTemplates);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _profile = profile;
        _options = profile.AnthropicOptions ?? new AnthropicOptions();
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _promptTemplates = promptTemplates;
        _promptRenderer = promptRenderer;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;

        var baseHttpClient = httpClientFactory.CreateClient(HttpClientNames.Llm);
        var trackingHandler = new AnthropicProgressTrackingHandler(() => _currentResetTimeout.Value);

        _client = new AnthropicClient
        {
            ApiKey = string.IsNullOrWhiteSpace(credentials.ApiKey) ? null : credentials.ApiKey,
            AuthToken = null,
            BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? EnvironmentUrl.Production : profile.BaseUrl.TrimEnd('/'),
            HttpClient = baseHttpClient,
            Handlers = [trackingHandler],
            MaxRetries = 0,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        Descriptor = new ProviderDescriptor(
            Id: _profile.Id,
            DisplayName: _profile.DisplayName,
            KindName: "Anthropic",
            Model: _profile.Model,
            IsConfigured: ComputeIsConfigured(profile, credentials));
    }

    public ProviderDescriptor Descriptor { get; }

    public void Dispose()
    {
        _client.Dispose();
    }

    public IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
        ProviderTranslateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timeout = TimeSpan.FromSeconds(_profile.TimeoutSeconds);
        return ProviderCallGuard.GuardStreamAsync(
            (token, resetTimeout) => TranslateStreamCoreAsync(request, resetTimeout, token),
            timeout,
            _timeProvider,
            _profile.Id,
            MapAnthropicException,
            cancellationToken);
    }

    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var targetLanguage = _settings.Current.Translation.PrimaryTargetLanguage;
        return ProviderCallGuard.RunTestAsync(this, targetLanguage, _timeProvider, _logger, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(_profile.TimeoutSeconds);
        return ProviderCallGuard.GuardAsync(
            ListModelsCoreAsync,
            timeout,
            _timeProvider,
            _profile.Id,
            MapAnthropicException,
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ListModelsCoreAsync(CancellationToken token)
    {
        if (!Descriptor.IsConfigured)
        {
            throw new IrisException(
                ErrorCode.NotConfigured,
                ErrorMessages.GetDefault(ErrorCode.NotConfigured),
                retryable: false,
                providerId: _profile.Id);
        }

        var modelIds = new List<string>();
        var page = await _client.Models.List(cancellationToken: token).ConfigureAwait(false);
        while (page != null)
        {
            if (page.Items != null)
            {
                foreach (var item in page.Items)
                {
                    if (!string.IsNullOrWhiteSpace(item.ID))
                    {
                        modelIds.Add(item.ID);
                    }
                    if (modelIds.Count >= 1000)
                    {
                        break;
                    }
                }
            }

            if (modelIds.Count >= 1000 || !page.HasNext())
            {
                break;
            }

            page = await page.Next(token).ConfigureAwait(false);
        }

        return modelIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async IAsyncEnumerable<TranslationChunk> TranslateStreamCoreAsync(
        ProviderTranslateRequest request,
        Action resetTimeout,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (!Descriptor.IsConfigured)
        {
            throw new IrisException(
                ErrorCode.NotConfigured,
                ErrorMessages.GetDefault(ErrorCode.NotConfigured),
                retryable: false,
                providerId: _profile.Id);
        }

        _currentResetTimeout.Value = resetTimeout;
        try
        {
            var fingerprint = LogText.Fingerprint(request.Text);
            LogTranslationStarted(_logger, _profile.Id, _profile.Model, fingerprint);

            var startTimestamp = _timeProvider.GetTimestamp();

            var template = _promptTemplates.Get(_profile.PromptTemplateId);
            var rendered = _promptRenderer.Render(template, request);

            var isFallbackEligible = _options.RefusalFallback == true &&
                string.IsNullOrWhiteSpace(_profile.BaseUrl) &&
                (_profile.Model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase) ||
                 _profile.Model.StartsWith("claude-fable-", StringComparison.OrdinalIgnoreCase));

            var streamItems = isFallbackEligible
                ? ReadBetaStreamAsync(rendered, token)
                : ReadNonBetaStreamAsync(rendered, token);

            long? inputTokens = null;
            long? outputTokens = null;
            string? rawStopReason = null;

            await foreach (var item in streamItems.WithCancellation(token).ConfigureAwait(false))
            {
                resetTimeout();

                if (item.InputTokens.HasValue)
                {
                    inputTokens = item.InputTokens.Value;
                }

                if (item.OutputTokens.HasValue)
                {
                    outputTokens = item.OutputTokens.Value;
                }

                if (!string.IsNullOrEmpty(item.StopReason))
                {
                    rawStopReason = item.StopReason;
                }

                if (!string.IsNullOrEmpty(item.Text))
                {
                    yield return new TranslationChunk(item.Text);
                }
            }

            if (rawStopReason == null)
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    retryable: false,
                    providerId: _profile.Id,
                    detail: "响应流意外结束");
            }

            if (string.Equals(rawStopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                throw new IrisException(
                    ErrorCode.ContentRefused,
                    ErrorMessages.GetDefault(ErrorCode.ContentRefused),
                    retryable: false,
                    providerId: _profile.Id,
                    detail: "模型拒绝回答（stop_reason=refusal）");
            }

            TranslationFinishReason finishReason;
            if (string.Equals(rawStopReason, "max_tokens", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawStopReason, "model_context_window_exceeded", StringComparison.OrdinalIgnoreCase))
            {
                finishReason = TranslationFinishReason.Truncated;
            }
            else if (string.Equals(rawStopReason, "end_turn", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rawStopReason, "stop_sequence", StringComparison.OrdinalIgnoreCase))
            {
                finishReason = TranslationFinishReason.Completed;
            }
            else
            {
                LogUnknownStopReason(_logger, _profile.Id, rawStopReason);
                finishReason = TranslationFinishReason.Completed;
            }

            TokenUsage? usage = null;
            if (inputTokens.HasValue || outputTokens.HasValue)
            {
                int? inTok = inputTokens.HasValue ? (int)Math.Min(inputTokens.Value, int.MaxValue) : null;
                int? outTok = outputTokens.HasValue ? (int)Math.Min(outputTokens.Value, int.MaxValue) : null;
                usage = new TokenUsage(inTok, outTok);
            }

            var elapsedMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LogTranslationFinished(_logger, _profile.Id, finishReason, elapsedMs, usage?.InputTokens, usage?.OutputTokens);

            yield return new TranslationChunk(string.Empty, Usage: usage, FinishReason: finishReason);
        }
        finally
        {
            _currentResetTimeout.Value = null;
        }
    }

    private async IAsyncEnumerable<StreamEventItem> ReadNonBetaStreamAsync(
        RenderedPrompt rendered,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = _profile.Model,
            MaxTokens = _profile.MaxOutputTokens ?? 16000,
            Messages = [new() { Role = Role.User, Content = rendered.User }],
            System = string.IsNullOrWhiteSpace(rendered.System) ? null : (MessageCreateParamsSystem)rendered.System,
            OutputConfig = string.IsNullOrWhiteSpace(_options.Effort) ? null : new OutputConfig { Effort = _options.Effort },
        };

        await foreach (var ev in _client.Messages.CreateStreaming(parameters, cancellationToken: ct).ConfigureAwait(false))
        {
            if (ev.TryPickStart(out var start))
            {
                yield return new StreamEventItem(null, start.Message.Usage.InputTokens, null, null);
            }
            else if (ev.TryPickContentBlockDelta(out var cbd) && cbd.Delta.TryPickText(out var textDelta))
            {
                yield return new StreamEventItem(textDelta.Text, null, null, null);
            }
            else if (ev.TryPickDelta(out var delta))
            {
                yield return new StreamEventItem(null, null, delta.Usage?.OutputTokens, delta.Delta.StopReason?.Raw());
            }
            else
            {
                yield return default;
            }
        }
    }

    private async IAsyncEnumerable<StreamEventItem> ReadBetaStreamAsync(
        RenderedPrompt rendered,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new Beta.MessageCreateParams
        {
            Model = _profile.Model,
            MaxTokens = _profile.MaxOutputTokens ?? 16000,
            Messages = [new() { Role = Beta.Role.User, Content = rendered.User }],
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Beta.Default(),
            System = string.IsNullOrWhiteSpace(rendered.System) ? null : (Beta.MessageCreateParamsSystem)rendered.System,
            OutputConfig = string.IsNullOrWhiteSpace(_options.Effort) ? null : new Beta.BetaOutputConfig { Effort = _options.Effort },
        };

        await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, cancellationToken: ct).ConfigureAwait(false))
        {
            if (ev.TryPickStart(out var start))
            {
                yield return new StreamEventItem(null, start.Message.Usage.InputTokens, null, null);
            }
            else if (ev.TryPickContentBlockDelta(out var cbd) && cbd.Delta.TryPickText(out var textDelta))
            {
                yield return new StreamEventItem(textDelta.Text, null, null, null);
            }
            else if (ev.TryPickDelta(out var delta))
            {
                yield return new StreamEventItem(null, null, delta.Usage?.OutputTokens, delta.Delta.StopReason?.Raw());
            }
            else
            {
                yield return default;
            }
        }
    }

    private IrisException? MapAnthropicException(Exception ex)
    {
        if (ex is AnthropicApiException apiEx)
        {
            return HttpErrorMapper.FromBody((int)apiEx.StatusCode, apiEx.ResponseBody, _profile.Id, [_credentials.ApiKey]);
        }

        if (ex is AnthropicSseException sseEx)
        {
            int statusCode = sseEx.ErrorType switch
            {
                ErrorType.RateLimitError => 429,
                ErrorType.OverloadedError => 529,
                ErrorType.ApiError => 500,
                ErrorType.InvalidRequestError => 400,
                ErrorType.AuthenticationError => 401,
                ErrorType.PermissionError => 403,
                ErrorType.NotFoundError => 404,
                ErrorType.BillingError => 402,
                ErrorType.TimeoutError => 504,
                _ => 500,
            };

            var message = sseEx.Message;
            string json = message;
            var firstQuote = message.IndexOf('\'');
            var lastQuote = message.LastIndexOf('\'');
            if (firstQuote >= 0 && lastQuote > firstQuote)
            {
                json = message.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
            }

            if (statusCode == 500 && IsRequestTooLarge(json))
            {
                statusCode = 413;
            }

            var irisEx = HttpErrorMapper.FromBody(statusCode, json, _profile.Id, [_credentials.ApiKey]);
            return new IrisException(
                irisEx.Code,
                irisEx.Message,
                innerException: null, // SECURITY RULE: never pass ex
                httpStatus: statusCode,
                retryable: irisEx.Retryable,
                providerId: _profile.Id,
                detail: irisEx.Detail);
        }

        if (ex is AnthropicInvalidDataException)
        {
            return new IrisException(
                ErrorCode.InvalidResponse,
                ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                innerException: null, // SECURITY RULE: never pass ex
                retryable: false,
                providerId: _profile.Id,
                detail: "无法解析的流事件");
        }

        if (ex is AnthropicIOException ioEx)
        {
            var inner = (Exception?)ioEx.InnerException ?? ioEx;
            var mapped = HttpErrorMapper.TryMapException(inner, _profile.Id);
            if (mapped != null)
            {
                return new IrisException(
                    mapped.Code,
                    mapped.Message,
                    innerException: null, // SECURITY RULE: never pass ex
                    httpStatus: mapped.HttpStatus,
                    retryable: mapped.Retryable,
                    providerId: _profile.Id,
                    detail: mapped.Detail);
            }

            return new IrisException(
                ErrorCode.NetworkUnavailable,
                ErrorMessages.GetDefault(ErrorCode.NetworkUnavailable),
                innerException: null,
                retryable: true,
                providerId: _profile.Id,
                detail: SecretRedactor.Redact(ioEx.Message));
        }

        return null;
    }

    private static bool ComputeIsConfigured(TranslationProviderProfile profile, ProviderCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            return false;
        }

        Uri? uri = null;
        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return true;
        }

        return uri != null && DynamicWebProxy.IsLocalAddress(uri);
    }

    internal static bool IsRequestTooLarge(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object)
            {
                if (errorProp.TryGetProperty("type", out var typeProp) &&
                    typeProp.ValueKind == JsonValueKind.String &&
                    typeProp.ValueEquals("request_too_large"))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // 非合法 JSON 或解析失败保持未知错误 500
        }

        return false;
    }

    private readonly record struct StreamEventItem(
        string? Text,
        long? InputTokens,
        long? OutputTokens,
        string? StopReason);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "开始翻译请求: ProviderId={ProviderId}, Model={Model}, Input={InputFingerprint}")]
    private static partial void LogTranslationStarted(ILogger logger, string providerId, string model, string inputFingerprint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "翻译请求结束: ProviderId={ProviderId}, FinishReason={FinishReason}, ElapsedMs={ElapsedMs:F1}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}")]
    private static partial void LogTranslationFinished(ILogger logger, string providerId, TranslationFinishReason finishReason, double elapsedMs, int? promptTokens, int? completionTokens);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "未知的 stop_reason: ProviderId={ProviderId}, Reason={RawReason}")]
    private static partial void LogUnknownStopReason(ILogger logger, string providerId, string? rawReason);
}

