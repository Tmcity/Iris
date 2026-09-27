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
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation.OpenAICompatible.Wire;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Translation.OpenAICompatible;

/// <summary>
/// OpenAI 兼容协议翻译服务，实现 ITranslationProvider 与 IModelCatalogSource。
/// </summary>
internal sealed partial class OpenAICompatibleTranslationProvider : ITranslationProvider, IModelCatalogSource
{
    private readonly TranslationProviderProfile _profile;
    private readonly OpenAICompatibleOptions _options;
    private readonly ProviderCredentials _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPromptTemplateProvider _promptTemplates;
    private readonly IPromptRenderer _promptRenderer;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenAICompatibleTranslationProvider> _logger;

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

        _profile = profile;
        _options = profile.OpenAIOptions ?? new OpenAICompatibleOptions();
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _promptTemplates = promptTemplates;
        _promptRenderer = promptRenderer;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<OpenAICompatibleTranslationProvider>();

        var isConfigured = Uri.TryCreate(_profile.BaseUrl, UriKind.Absolute, out var baseUri)
            && (baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(_profile.Model)
            && (!string.IsNullOrWhiteSpace(_credentials.ApiKey) || DynamicWebProxy.IsLocalAddress(baseUri));

        Descriptor = new ProviderDescriptor(_profile.Id, _profile.DisplayName, "OpenAICompatible", _profile.Model, isConfigured);
    }

    public ProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(
        ProviderTranslateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Descriptor.IsConfigured)
        {
            throw new IrisException(
                ErrorCode.NotConfigured,
                ErrorMessages.GetDefault(ErrorCode.NotConfigured),
                retryable: false,
                providerId: _profile.Id);
        }

        var timeout = TimeSpan.FromSeconds(_profile.TimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await using var enumerator = TranslateStreamCoreAsync(request, timeoutCts, timeout, linkedCts.Token)
            .GetAsyncEnumerator(linkedCts.Token);

        while (true)
        {
            TranslationChunk current;
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                if (hasNext)
                {
                    current = enumerator.Current;
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                var classified = ClassifyException(ex, timeoutCts, timeout, cancellationToken);
                if (ReferenceEquals(classified, ex))
                {
                    throw;
                }
                throw classified;
            }

            yield return current;
        }
    }

    public async Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
    {
        var targetLanguage = _settings.Current.Translation.PrimaryTargetLanguage;
        var request = new ProviderTranslateRequest("Hello, world.", LanguageCodes.English, targetLanguage);

        var startTimestamp = _timeProvider.GetTimestamp();
        var sb = new StringBuilder();

        try
        {
            await foreach (var chunk in TranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    sb.Append(chunk.Delta);
                }
            }

            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            return new ProviderTestResult(true, elapsed, Sample: sb.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IrisException ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            return new ProviderTestResult(false, elapsed, Error: ex.ToErrorInfo());
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            var exType = ex.GetType().Name;
            LogTestFailed(_logger, _profile.Id, exType, ex);
            var errorInfo = new ErrorInfo(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), exType);
            return new ProviderTestResult(false, elapsed, Error: errorInfo);
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (!Descriptor.IsConfigured)
        {
            throw new IrisException(
                ErrorCode.NotConfigured,
                ErrorMessages.GetDefault(ErrorCode.NotConfigured),
                retryable: false,
                providerId: _profile.Id);
        }

        var timeout = TimeSpan.FromSeconds(_profile.TimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var baseUrl = _profile.BaseUrl?.TrimEnd('/') ?? string.Empty;
            var requestUri = $"{baseUrl}/models";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);

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

            var client = _httpClientFactory.CreateClient(HttpClientNames.Llm);
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await HttpErrorMapper.FromResponseAsync(response, _profile.Id, [_credentials.ApiKey], linkedCts.Token).ConfigureAwait(false);
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var limitedStream = new LimitedReadStream(responseStream, SseParser.DefaultMaxResponseBytes);

            ModelListResponse? modelList;
            try
            {
                modelList = await JsonSerializer.DeserializeAsync<ModelListResponse>(limitedStream, ProviderJson.Options, linkedCts.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    innerException: ex,
                    retryable: false,
                    providerId: _profile.Id,
                    detail: SecretRedactor.Redact(ex.Message));
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
        catch (Exception ex)
        {
            var classified = ClassifyException(ex, timeoutCts, timeout, cancellationToken);
            if (ReferenceEquals(classified, ex))
            {
                throw;
            }
            throw classified;
        }
    }

    private async IAsyncEnumerable<TranslationChunk> TranslateStreamCoreAsync(
        ProviderTranslateRequest request,
        CancellationTokenSource timeoutCts,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken linkedToken)
    {
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
                    innerException: ex,
                    retryable: false,
                    providerId: _profile.Id,
                    detail: SecretRedactor.Redact(ex.Message));
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
                    innerException: ex,
                    retryable: false,
                    providerId: _profile.Id,
                    detail: SecretRedactor.Redact(ex.Message));
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
            if (completionResponse.Usage != null && completionResponse.Usage.PromptTokens.HasValue && completionResponse.Usage.CompletionTokens.HasValue)
            {
                usage = new TokenUsage(completionResponse.Usage.PromptTokens.Value, completionResponse.Usage.CompletionTokens.Value);
            }

            var finishReasonText = finishReason.ToString();
            var elapsedMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LogTranslationFinished(_logger, _profile.Id, finishReasonText, elapsedMs, usage?.InputTokens, usage?.OutputTokens);

            yield return new TranslationChunk(finalDelta, Usage: usage, FinishReason: finishReason);
        }
        else
        {
            var stripper = _options.StripThinkTags ? new ThinkTagStripper() : null;
            string? lastFinishReason = null;
            TokenUsage? usage = null;
            var hasDone = false;

            using var responseStream = await response.Content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false);
            await foreach (var sseEvent in SseParser.ReadEventsAsync(responseStream, linkedToken).ConfigureAwait(false))
            {
                timeoutCts.CancelAfter(timeout);

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
                        innerException: ex,
                        retryable: false,
                        providerId: _profile.Id,
                        detail: SecretRedactor.Redact(ex.Message));
                }

                if (chunk == null)
                {
                    continue;
                }

                if (chunk.Usage != null && chunk.Usage.PromptTokens.HasValue && chunk.Usage.CompletionTokens.HasValue)
                {
                    usage = new TokenUsage(chunk.Usage.PromptTokens.Value, chunk.Usage.CompletionTokens.Value);
                }

                if (chunk.Choices == null || chunk.Choices.Count == 0)
                {
                    continue;
                }

                var choice = chunk.Choices[0];
                if (!string.IsNullOrEmpty(choice.FinishReason))
                {
                    lastFinishReason = choice.FinishReason;
                    if (string.Equals(choice.FinishReason, FinishReasons.ContentFilter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(choice.FinishReason, "sensitive", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(choice.FinishReason, "insufficient_system_resource", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(choice.FinishReason, "network_error", StringComparison.OrdinalIgnoreCase))
                    {
                        MapFinishReason(choice.FinishReason);
                    }
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

            if (!hasDone && string.IsNullOrEmpty(lastFinishReason))
            {
                throw new IrisException(
                    ErrorCode.InvalidResponse,
                    ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                    retryable: false,
                    providerId: _profile.Id,
                    detail: "响应流意外结束");
            }

            var finalReason = MapFinishReason(lastFinishReason);
            var remaining = stripper?.Flush() ?? string.Empty;

            var finishReasonText = finalReason.ToString();
            var elapsedMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LogTranslationFinished(_logger, _profile.Id, finishReasonText, elapsedMs, usage?.InputTokens, usage?.OutputTokens);

            yield return new TranslationChunk(remaining, Usage: usage, FinishReason: finalReason);
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

    private Exception ClassifyException(
        Exception ex,
        CancellationTokenSource timeoutCts,
        TimeSpan timeout,
        CancellationToken userToken)
    {
        if (userToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException oce && oce.CancellationToken == userToken)
            {
                return oce;
            }
            return new OperationCanceledException(userToken);
        }

        if (timeoutCts.IsCancellationRequested)
        {
            return HttpErrorMapper.Timeout(_profile.Id, timeout);
        }

        if (ex is IrisException irisEx)
        {
            return irisEx;
        }

        var mapped = HttpErrorMapper.TryMapException(ex, _profile.Id);
        if (mapped != null)
        {
            return mapped;
        }

        return ex;
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

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "测试翻译异常: ProviderId={ProviderId}, Exception={ExceptionType}")]
    private static partial void LogTestFailed(ILogger logger, string providerId, string exceptionType, Exception exception);
}
