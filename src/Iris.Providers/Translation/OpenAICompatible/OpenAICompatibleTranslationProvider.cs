using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Diagnostics;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation.OpenAICompatible.Wire;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Translation.OpenAICompatible;

internal sealed partial class OpenAICompatibleTranslationProvider : ITranslationProvider, IModelCatalogSource
{
    private readonly TranslationProviderProfile _profile;
    private readonly ProviderCredentials _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPromptTemplateProvider _promptTemplates;
    private readonly IPromptRenderer _promptRenderer;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenAICompatibleTranslationProvider> _logger;
    private readonly OpenAICompatibleOptions _options;

    public OpenAICompatibleTranslationProvider(
        TranslationProviderProfile profile,
        ProviderCredentials credentials,
        IHttpClientFactory httpClientFactory,
        IPromptTemplateProvider promptTemplates,
        IPromptRenderer promptRenderer,
        ISettingsService settings,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(promptTemplates);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (profile.Kind != TranslationProviderKind.OpenAICompatible)
        {
            throw new ArgumentException($"Provider kind must be OpenAICompatible, but was '{profile.Kind}'.", nameof(profile));
        }

        _profile = profile;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _promptTemplates = promptTemplates;
        _promptRenderer = promptRenderer;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<OpenAICompatibleTranslationProvider>();
        _options = profile.OpenAIOptions ?? new OpenAICompatibleOptions();

        var isConfigured = ComputeIsConfigured(profile, credentials);
        Descriptor = new ProviderDescriptor(profile.Id, profile.DisplayName, "OpenAICompatible", profile.Model, isConfigured);
    }

    public ProviderDescriptor Descriptor { get; }

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
            mapSpecific: null,
            cancellationToken);
    }

    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
    {
        var targetLanguage = _settings.Current.Translation.PrimaryTargetLanguage;
        return ProviderCallGuard.RunTestAsync(this, targetLanguage, _timeProvider, _logger, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_profile.TimeoutSeconds);
        return ProviderCallGuard.GuardAsync(
            ListModelsCoreAsync,
            timeout,
            _timeProvider,
            _profile.Id,
            mapSpecific: null,
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

        var baseUrl = _profile.BaseUrl?.TrimEnd('/') ?? string.Empty;
        var requestUri = $"{baseUrl}/models";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);

        ApplyHeaders(httpRequest);

        var client = _httpClientFactory.CreateClient(HttpClientNames.Llm);
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await HttpErrorMapper.FromResponseAsync(response, _profile.Id, [_credentials.ApiKey], token).ConfigureAwait(false);
        }

        using var responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var limitedStream = new LimitedReadStream(responseStream, SseParser.DefaultMaxResponseBytes);

        ModelListResponse? modelList;
        try
        {
            modelList = await JsonSerializer.DeserializeAsync<ModelListResponse>(limitedStream, ProviderJson.Options, token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new IrisException(
                ErrorCode.InvalidResponse,
                ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                innerException: null,
                retryable: false,
                providerId: _profile.Id,
                detail: SafeHttpDiagnostics.FormatJsonExceptionDetail(ex));
        }

        if (modelList?.Data == null)
        {
            throw new IrisException(
                ErrorCode.InvalidResponse,
                ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                retryable: false,
                providerId: _profile.Id,
                detail: "模型列表 data 为空");
        }

        var models = modelList.Data
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return models;
    }

    private async IAsyncEnumerable<TranslationChunk> TranslateStreamCoreAsync(
        ProviderTranslateRequest request,
        Action resetTimeout,
        [EnumeratorCancellation] CancellationToken linkedToken)
    {
        if (!Descriptor.IsConfigured)
        {
            throw new IrisException(
                ErrorCode.NotConfigured,
                ErrorMessages.GetDefault(ErrorCode.NotConfigured),
                retryable: false,
                providerId: _profile.Id);
        }

        var startTimestamp = _timeProvider.GetTimestamp();
        var fingerprint = LogText.Fingerprint(request.Text);
        LogTranslationStarted(_logger, _profile.Id, _profile.Model, fingerprint);

        var baseUrl = _profile.BaseUrl?.TrimEnd('/') ?? string.Empty;
        var path = _options.ChatCompletionsPath?.TrimStart('/') ?? "chat/completions";
        var requestUri = $"{baseUrl}/{path}";

        var template = _promptTemplates.Get(_profile.PromptTemplateId);
        var rendered = _promptRenderer.Render(template, request);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(rendered.System))
        {
            messages.Add(new ChatMessage(ChatRoles.System, rendered.System));
        }
        messages.Add(new ChatMessage(ChatRoles.User, rendered.User));

        int? maxTokens = null;
        int? maxCompletionTokens = null;
        if (_profile.MaxOutputTokens.HasValue)
        {
            if (_options.MaxTokensField == MaxTokensField.MaxCompletionTokens)
            {
                maxCompletionTokens = _profile.MaxOutputTokens.Value;
            }
            else
            {
                maxTokens = _profile.MaxOutputTokens.Value;
            }
        }

        var stream = _options.Stream;
        StreamOptions? streamOptions = (stream && _options.IncludeStreamUsage)
            ? new StreamOptions(IncludeUsage: true)
            : null;

        var requestBody = new ChatCompletionRequest
        {
            Model = _profile.Model,
            Messages = messages,
            Stream = stream,
            Temperature = _profile.Temperature,
            MaxTokens = maxTokens,
            MaxCompletionTokens = maxCompletionTokens,
            StreamOptions = streamOptions,
        };

        var utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(requestBody, ProviderJson.Options);
        var content = new ByteArrayContent(utf8Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };

        ApplyHeaders(httpRequest);

        var client = _httpClientFactory.CreateClient(HttpClientNames.Llm);
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await HttpErrorMapper.FromResponseAsync(response, _profile.Id, [_credentials.ApiKey], linkedToken).ConfigureAwait(false);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);

        if (isJson)
        {
            using var responseStream = await response.Content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false);
            using var limitedStream = new LimitedReadStream(responseStream, SseParser.DefaultMaxResponseBytes);
            using var reader = new StreamReader(limitedStream, Encoding.UTF8);
            var bodyText = await reader.ReadToEndAsync(linkedToken).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(bodyText);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _))
                {
                    throw HttpErrorMapper.FromBody((int)response.StatusCode, bodyText, _profile.Id, [_credentials.ApiKey]);
                }
            }
            catch (JsonException ex)
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    innerException: null,
                    retryable: false,
                    providerId: _profile.Id,
                    detail: SafeHttpDiagnostics.FormatJsonExceptionDetail(ex));
            }

            ChatCompletionResponse? completionResponse;
            try
            {
                completionResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(bodyText, ProviderJson.Options);
            }
            catch (JsonException ex)
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    innerException: null,
                    retryable: false,
                    providerId: _profile.Id,
                    detail: SafeHttpDiagnostics.FormatJsonExceptionDetail(ex));
            }

            if (completionResponse?.Choices == null || completionResponse.Choices.Count == 0)
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    retryable: false,
                    providerId: _profile.Id,
                    detail: "响应 choices 为空");
            }

            var choice = completionResponse.Choices[0];
            var contentText = choice.Message?.Content;
            if (contentText == null)
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    retryable: false,
                    providerId: _profile.Id,
                    detail: "响应 content 为空");
            }

            var finishReason = MapFinishReason(choice.FinishReason);
            string finalDelta;
            if (_options.StripThinkTags)
            {
                var stripper = new ThinkTagStripper();
                finalDelta = stripper.Process(contentText) + stripper.Flush();
            }
            else
            {
                finalDelta = contentText;
            }

            TokenUsage? usage = null;
            if (completionResponse.Usage != null && (completionResponse.Usage.PromptTokens.HasValue || completionResponse.Usage.CompletionTokens.HasValue))
            {
                usage = new TokenUsage(completionResponse.Usage.PromptTokens, completionResponse.Usage.CompletionTokens);
            }

            var finishReasonText = finishReason.ToString();
            var elapsedMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LogTranslationFinished(_logger, _profile.Id, finishReasonText, elapsedMs, usage?.InputTokens, usage?.OutputTokens);

            yield return new TranslationChunk(finalDelta, Usage: usage, FinishReason: finishReason);
        }
        else
        {
            var stripper = _options.StripThinkTags ? new ThinkTagStripper() : null;
            TranslationFinishReason? lastFinishReason = null;
            TokenUsage? usage = null;
            var hasDone = false;
            var eventCount = 0;

            using var responseStream = await response.Content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false);
            await foreach (var sseEvent in SseParser.ReadEventsAsync(responseStream, linkedToken).ConfigureAwait(false))
            {
                eventCount++;
                resetTimeout();

                if (string.Equals(sseEvent.Data, "[DONE]", StringComparison.Ordinal))
                {
                    hasDone = true;
                    break;
                }

                if (string.Equals(sseEvent.EventType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw HttpErrorMapper.FromBody(200, sseEvent.Data, _profile.Id, [_credentials.ApiKey]);
                }

                ChatCompletionChunk? chunk;
                try
                {
                    using var doc = JsonDocument.Parse(sseEvent.Data);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _))
                    {
                        throw HttpErrorMapper.FromBody(200, sseEvent.Data, _profile.Id, [_credentials.ApiKey]);
                    }
                    chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(doc.RootElement, ProviderJson.Options);
                }
                catch (JsonException ex)
                {
                    throw new IrisException(
                        ErrorCode.InvalidResponse,
                        ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                        innerException: null,
                        retryable: false,
                        providerId: _profile.Id,
                        detail: SafeHttpDiagnostics.FormatJsonExceptionDetail(ex));
                }

                if (chunk == null)
                {
                    continue;
                }

                if (chunk.Usage != null && (chunk.Usage.PromptTokens.HasValue || chunk.Usage.CompletionTokens.HasValue))
                {
                    usage = new TokenUsage(chunk.Usage.PromptTokens, chunk.Usage.CompletionTokens);
                }

                if (chunk.Choices == null || chunk.Choices.Count == 0)
                {
                    continue;
                }

                var choice = chunk.Choices[0];
                if (!string.IsNullOrEmpty(choice.FinishReason))
                {
                    lastFinishReason = MapFinishReason(choice.FinishReason);
                }

                var deltaContent = choice.Delta?.Content;
                if (!string.IsNullOrEmpty(deltaContent))
                {
                    if (stripper != null)
                    {
                        var processed = stripper.Process(deltaContent);
                        if (!string.IsNullOrEmpty(processed))
                        {
                            yield return new TranslationChunk(processed);
                        }
                    }
                    else
                    {
                        yield return new TranslationChunk(deltaContent);
                    }
                }
            }

            if (!hasDone && lastFinishReason == null)
            {
                var detail = (eventCount == 0 && !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    ? $"服务未返回事件流（Content-Type: {mediaType ?? "无"}），请检查 BaseUrl"
                    : "响应流意外结束";

                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    retryable: false,
                    providerId: _profile.Id,
                    detail: detail);
            }

            var finalReason = lastFinishReason ?? TranslationFinishReason.Completed;
            var remaining = stripper?.Flush() ?? string.Empty;

            var finishReasonText = finalReason.ToString();
            var elapsedMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LogTranslationFinished(_logger, _profile.Id, finishReasonText, elapsedMs, usage?.InputTokens, usage?.OutputTokens);

            yield return new TranslationChunk(remaining, Usage: usage, FinishReason: finalReason);
        }
    }

    private void ApplyHeaders(HttpRequestMessage httpRequest)
    {
        if (!string.IsNullOrWhiteSpace(_credentials.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);
        }

        if (_options.ExtraHeaders != null)
        {
            foreach (var (headerName, headerValue) in _options.ExtraHeaders)
            {
                if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    LogIgnoredAuthorizationHeader(_logger, headerName);
                    continue;
                }
                if (!httpRequest.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    LogFailedToAddHeader(_logger, headerName);
                }
            }
        }
    }

    private TranslationFinishReason MapFinishReason(string? rawReason)
    {
        if (string.IsNullOrEmpty(rawReason) || string.Equals(rawReason, FinishReasons.Stop, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationFinishReason.Completed;
        }

        if (string.Equals(rawReason, FinishReasons.Length, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationFinishReason.Truncated;
        }

        if (string.Equals(rawReason, FinishReasons.ContentFilter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawReason, "sensitive", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(
                ErrorCode.ContentRefused,
                ErrorMessages.GetDefault(ErrorCode.ContentRefused),
                retryable: false,
                providerId: _profile.Id,
                detail: $"内容被安全审核拦截（{rawReason}）");
        }

        if (string.Equals(rawReason, "insufficient_system_resource", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawReason, "network_error", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(
                ErrorCode.ProviderUnavailable,
                ErrorMessages.GetDefault(ErrorCode.ProviderUnavailable),
                retryable: true,
                providerId: _profile.Id,
                detail: $"服务端异常中断（{rawReason}）");
        }

        LogUnknownFinishReason(_logger, _profile.Id, rawReason);
        return TranslationFinishReason.Completed;
    }

    private static bool ComputeIsConfigured(TranslationProviderProfile profile, ProviderCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.BaseUrl) || !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            return true;
        }

        return DynamicWebProxy.IsLocalAddress(uri);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "开始翻译请求: ProviderId={ProviderId}, Model={Model}, Input={InputFingerprint}")]
    private static partial void LogTranslationStarted(ILogger logger, string providerId, string model, string inputFingerprint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "翻译请求结束: ProviderId={ProviderId}, FinishReason={FinishReason}, ElapsedMs={ElapsedMs:F1}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}")]
    private static partial void LogTranslationFinished(ILogger logger, string providerId, string finishReason, double elapsedMs, int? promptTokens, int? completionTokens);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "忽略 ExtraHeaders 中的 Authorization 请求头: {HeaderName}")]
    private static partial void LogIgnoredAuthorizationHeader(ILogger logger, string headerName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "ExtraHeaders 请求头添加失败: {HeaderName}")]
    private static partial void LogFailedToAddHeader(ILogger logger, string headerName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "未知的 finish_reason: ProviderId={ProviderId}, Reason={RawReason}")]
    private static partial void LogUnknownFinishReason(ILogger logger, string providerId, string? rawReason);
}
