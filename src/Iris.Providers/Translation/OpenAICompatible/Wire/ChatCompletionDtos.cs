using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Providers.Translation.OpenAICompatible.Wire;

public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}

public static class FinishReasons
{
    public const string Stop = "stop";
    public const string Length = "length";
    public const string ContentFilter = "content_filter";
}

/// <summary>POST {BaseUrl}/{ChatCompletionsPath}，Header：Authorization: Bearer {ApiKey}（Key 为空则不发送该头）。</summary>
public sealed record ChatCompletionRequest
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("messages")] public required IReadOnlyList<ChatMessage> Messages { get; init; }
    [JsonPropertyName("stream")] public bool Stream { get; init; }
    [JsonPropertyName("temperature")] public double? Temperature { get; init; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
    [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; init; }
    [JsonPropertyName("stream_options")] public StreamOptions? StreamOptions { get; init; }
}

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record StreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

/// <summary>流式响应：每行 "data: {ChatCompletionChunk}"，以 "data: [DONE]" 结束。</summary>
public sealed record ChatCompletionChunk
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("choices")] public IReadOnlyList<ChunkChoice>? Choices { get; init; }
    [JsonPropertyName("usage")] public UsageDto? Usage { get; init; }
}

public sealed record ChunkChoice
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("delta")] public ChunkDelta? Delta { get; init; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

public sealed record ChunkDelta
{
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }

    /// <summary>DeepSeek-R1 等推理模型的思考内容：必须忽略，不得输出到译文。</summary>
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }
}

/// <summary>非流式响应（OpenAIOptions.Stream=false）。</summary>
public sealed record ChatCompletionResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("choices")] public IReadOnlyList<ResponseChoice>? Choices { get; init; }
    [JsonPropertyName("usage")] public UsageDto? Usage { get; init; }
}

public sealed record ResponseChoice
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("message")] public ResponseMessage? Message { get; init; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

public sealed record ResponseMessage
{
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }
}

public sealed record UsageDto
{
    [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; init; }
    [JsonPropertyName("total_tokens")] public int? TotalTokens { get; init; }
}

/// <summary>错误响应体。code 在不同服务中可能是字符串或数字，故用 JsonElement。</summary>
public sealed record ErrorEnvelope
{
    [JsonPropertyName("error")] public ErrorBody? Error { get; init; }
}

public sealed record ErrorBody
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("code")] public JsonElement? Code { get; init; }
    [JsonPropertyName("param")] public string? Param { get; init; }
}

/// <summary>GET {BaseUrl}/models。</summary>
public sealed record ModelListResponse
{
    [JsonPropertyName("data")] public IReadOnlyList<ModelItem>? Data { get; init; }
}

public sealed record ModelItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("owned_by")] public string? OwnedBy { get; init; }
}
