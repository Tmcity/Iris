using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Providers.Http;

/// <summary>IHttpClientFactory 命名客户端。</summary>
public static class HttpClientNames
{
    /// <summary>大模型流式请求：不设 HttpClient.Timeout（= Infinite），超时由 Provider 的 CTS 控制；仅在响应头返回前重试。</summary>
    public const string Llm = "iris.llm";

    /// <summary>OCR 与 Token 请求：标准弹性管道（重试 + 总超时）。</summary>
    public const string Ocr = "iris.ocr";

    /// <summary>公开桌面更新客户端：复用版本化代理/响应所有权，不携带Provider或仓库凭据，不应用业务重试。</summary>
    public const string Updates = "iris.updates";
}

/// <summary>线协议 JSON 选项：属性名一律由 [JsonPropertyName] 显式指定，写出时忽略 null。</summary>
public static class ProviderJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>一条 Server-Sent Event（由 SseParser 从响应流解析）。</summary>
/// <param name="EventType">"event:" 字段；未指定时为 "message"（WHATWG 规范的默认值；OpenAI 兼容协议不带 event 字段，因此总是 "message"）。</param>
/// <param name="Data">多行 "data:" 以 \n 连接后的内容。</param>
public readonly record struct SseEvent(string? EventType, string Data, string? Id);
