namespace Iris.Core.Settings;

public enum TranslationProviderKind
{
    /// <summary>OpenAI Chat Completions 兼容协议（OpenAI / DeepSeek / 通义千问 / Kimi / 智谱 / SiliconFlow / Ollama / Gemini 兼容端点 …）。</summary>
    OpenAICompatible = 1,

    /// <summary>Anthropic Messages API（使用官方 Anthropic C# SDK）。</summary>
    Anthropic = 2,
}

public enum MaxTokensField
{
    /// <summary>发送 "max_tokens"（绝大多数兼容服务）。</summary>
    MaxTokens,

    /// <summary>发送 "max_completion_tokens"（OpenAI 推理模型）。</summary>
    MaxCompletionTokens,
}

public sealed record OpenAICompatibleOptions
{
    public bool Stream { get; init; } = true;

    /// <summary>发送 stream_options.include_usage=true。部分兼容服务不支持，默认关闭。</summary>
    public bool IncludeStreamUsage { get; init; }

    public MaxTokensField MaxTokensField { get; init; } = MaxTokensField.MaxTokens;

    /// <summary>剥离输出中的 &lt;think&gt;…&lt;/think&gt; 段（Qwen3 / 本地推理模型）。</summary>
    public bool StripThinkTags { get; init; } = true;

    /// <summary>相对 BaseUrl 的路径。</summary>
    public string ChatCompletionsPath { get; init; } = "chat/completions";

    /// <summary>附加请求头（如 OpenRouter 的 HTTP-Referer）。不得用于存放密钥。</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}

public sealed record AnthropicOptions
{
    /// <summary>output_config.effort：low|medium|high|xhigh|max。null = 不发送（不支持 effort 的模型如 Haiku 4.5 必须为 null）。</summary>
    public string? Effort { get; init; } = "low";

    /// <summary>启用服务端拒答回退（beta server-side fallback）。</summary>
    public bool RefusalFallback { get; init; } = true;
}

/// <summary>翻译服务配置。API Key 不在此处，存放于 ISecretStore（键名见 SecretKeys）。</summary>
public sealed record TranslationProviderProfile
{
    /// <summary>稳定 Id："tp-" + Guid("N") 前 12 位；创建后不可修改。</summary>
    public required string Id { get; init; }

    /// <summary>null = 兼容旧的按 Profile.Id 存储的凭据；非 null = 精确的不可变凭据集引用。
    /// 引用非法、缺失或内容损坏时视为未配置，禁止回退旧键。格式与发布协议见 PROVIDER_REGISTRY.md。</summary>
    public string? CredentialSetId { get; init; }

    public required TranslationProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>结果卡片排序，升序。</summary>
    public int Order { get; init; }

    /// <summary>OpenAICompatible 必填（如 https://api.deepseek.com/v1）；Anthropic 可选（反代地址），null = 官方地址。</summary>
    public string? BaseUrl { get; init; }

    public required string Model { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>null = 不发送（使用服务端默认值）。Anthropic 必填，null 时使用 16000。</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>null = 不发送。Anthropic Provider 始终忽略此项（新模型拒绝采样参数）。</summary>
    public double? Temperature { get; init; }

    /// <summary>null = 使用 Settings.Translation.DefaultPromptTemplateId。</summary>
    public string? PromptTemplateId { get; init; }

    public OpenAICompatibleOptions? OpenAIOptions { get; init; }
    public AnthropicOptions? AnthropicOptions { get; init; }
}

public enum OcrProviderKind
{
    BaiduCloud = 1,
    WindowsLocal = 2,
}

public enum BaiduOcrMode
{
    /// <summary>通用文字识别（标准版）general_basic。</summary>
    GeneralBasic,

    /// <summary>通用文字识别（高精度版）accurate_basic。</summary>
    AccurateBasic,

    /// <summary>通用文字识别（标准含位置版）general。</summary>
    General,

    /// <summary>通用文字识别（高精度含位置版）accurate。</summary>
    Accurate,
}

public sealed record BaiduOcrOptions
{
    public BaiduOcrMode Mode { get; init; } = BaiduOcrMode.AccurateBasic;
    public bool DetectDirection { get; init; } = true;

    /// <summary>请求段落信息（paragraph=true），用于更好的段落合并。</summary>
    public bool Paragraph { get; init; } = true;

    /// <summary>显式 language_type；null = 按 OcrRequest.LanguageHint 映射。</summary>
    public string? LanguageType { get; init; }

    public string BaseUrl { get; init; } = "https://aip.baidubce.com";
}

public sealed record WindowsOcrOptions
{
    /// <summary>BCP-47 语言标签，如 "zh-Hans-CN"；null = 按 LanguageHint，再退回用户配置语言。</summary>
    public string? LanguageTag { get; init; }
}

/// <summary>OCR 服务配置。凭据存放于 ISecretStore（百度：ApiKey + SecretKey）。</summary>
public sealed record OcrProviderProfile
{
    public required string Id { get; init; }
    /// <summary>null = 兼容旧的按 Profile.Id 存储的凭据；非 null = 精确的不可变凭据集引用。
    /// 引用非法、缺失或内容损坏时视为未配置，禁止回退旧键。格式与发布协议见 PROVIDER_REGISTRY.md。</summary>
    public string? CredentialSetId { get; init; }
    public required OcrProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 15;
    public BaiduOcrOptions? BaiduOptions { get; init; }
    public WindowsOcrOptions? WindowsOptions { get; init; }
}

public static class BuiltInProviderIds
{
    public const string BaiduOcr = "ocr-baidu";
    public const string WindowsOcr = "ocr-windows";
}

/// <summary>设置页“添加翻译服务”预设。SuggestedModel 为 null 时由用户填写或通过 IModelCatalogSource 拉取。</summary>
public sealed record TranslationProviderPreset(
    string Key,
    string DisplayName,
    TranslationProviderKind Kind,
    string? BaseUrl,
    string? SuggestedModel,
    bool RequiresApiKey);

public static class TranslationProviderPresets
{
    public static IReadOnlyList<TranslationProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", TranslationProviderKind.OpenAICompatible, "https://api.openai.com/v1", null, true),
        new("anthropic", "Claude (Anthropic)", TranslationProviderKind.Anthropic, null, "claude-opus-5", true),
        new("deepseek", "DeepSeek", TranslationProviderKind.OpenAICompatible, "https://api.deepseek.com", null, true),
        new("dashscope", "通义千问 (DashScope)", TranslationProviderKind.OpenAICompatible, "https://dashscope.aliyuncs.com/compatible-mode/v1", null, true),
        new("moonshot", "Kimi (Moonshot)", TranslationProviderKind.OpenAICompatible, "https://api.moonshot.cn/v1", null, true),
        new("zhipu", "智谱 GLM", TranslationProviderKind.OpenAICompatible, "https://open.bigmodel.cn/api/paas/v4", null, true),
        new("siliconflow", "SiliconFlow", TranslationProviderKind.OpenAICompatible, "https://api.siliconflow.cn/v1", null, true),
        new("gemini", "Gemini (OpenAI 兼容端点)", TranslationProviderKind.OpenAICompatible, "https://generativelanguage.googleapis.com/v1beta/openai", null, true),
        new("ollama", "Ollama (本地)", TranslationProviderKind.OpenAICompatible, "http://localhost:11434/v1", null, false),
        new("custom", "自定义 OpenAI 兼容服务", TranslationProviderKind.OpenAICompatible, null, null, true),
    ];
}
