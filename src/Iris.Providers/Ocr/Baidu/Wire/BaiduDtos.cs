using System.Text.Json.Serialization;

namespace Iris.Providers.Ocr.Baidu.Wire;

public static class BaiduEndpoints
{
    /// <summary>POST {BaseUrl}/oauth/2.0/token?grant_type=client_credentials&amp;client_id={ApiKey}&amp;client_secret={SecretKey}</summary>
    public const string TokenPath = "/oauth/2.0/token";

    /// <summary>以下均为 POST {BaseUrl}{Path}?access_token={token}，Content-Type: application/x-www-form-urlencoded。</summary>
    public const string GeneralBasicPath = "/rest/2.0/ocr/v1/general_basic";
    public const string AccurateBasicPath = "/rest/2.0/ocr/v1/accurate_basic";
    public const string GeneralPath = "/rest/2.0/ocr/v1/general";
    public const string AccuratePath = "/rest/2.0/ocr/v1/accurate";
}

/// <summary>表单字段名。image = 图像 Base64（不含 data: 前缀），由 FormUrlEncodedContent 负责 URL 编码。</summary>
public static class BaiduFormFields
{
    public const string Image = "image";
    public const string LanguageType = "language_type";
    public const string DetectDirection = "detect_direction";
    public const string Paragraph = "paragraph";
    public const string Probability = "probability";
}

public sealed record BaiduTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }

    /// <summary>有效期（秒），通常为 2592000（30 天）。</summary>
    [JsonPropertyName("expires_in")] public long? ExpiresIn { get; init; }

    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    /// <summary>诊断不回显Token、scope或服务端错误正文。</summary>
    public override string ToString() => $"BaiduTokenResponse {{ HasAccessToken = {!string.IsNullOrEmpty(AccessToken)}, ExpiresIn = {ExpiresIn}, HasError = {!string.IsNullOrEmpty(Error)} }}";
}

/// <summary>识别响应。注意：业务错误也以 HTTP 200 返回，必须检查 ErrorCode。</summary>
public sealed record BaiduOcrResponse
{
    [JsonPropertyName("log_id")] public ulong? LogId { get; init; }
    [JsonPropertyName("direction")] public int? Direction { get; init; }
    [JsonPropertyName("words_result_num")] public int? WordsResultNum { get; init; }
    [JsonPropertyName("words_result")] public IReadOnlyList<BaiduWord>? WordsResult { get; init; }
    [JsonPropertyName("paragraphs_result_num")] public int? ParagraphsResultNum { get; init; }
    [JsonPropertyName("paragraphs_result")] public IReadOnlyList<BaiduParagraph>? ParagraphsResult { get; init; }
    [JsonPropertyName("error_code")] public int? ErrorCode { get; init; }
    [JsonPropertyName("error_msg")] public string? ErrorMsg { get; init; }
    /// <summary>诊断仅保留数值摘要，不回显识别正文或错误文本。</summary>
    public override string ToString() => $"BaiduOcrResponse {{ WordsResultNum = {WordsResultNum}, ErrorCode = {ErrorCode} }}";
}

public sealed record BaiduWord
{
    [JsonPropertyName("words")] public string? Words { get; init; }

    /// <summary>仅 general / accurate（含位置版）返回。</summary>
    [JsonPropertyName("location")] public BaiduLocation? Location { get; init; }

    [JsonPropertyName("probability")] public BaiduProbability? Probability { get; init; }
}

public sealed record BaiduLocation
{
    [JsonPropertyName("left")] public int Left { get; init; }
    [JsonPropertyName("top")] public int Top { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
}

public sealed record BaiduProbability
{
    [JsonPropertyName("average")] public double? Average { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("variance")] public double? Variance { get; init; }
}

/// <summary>一个段落由若干 words_result 下标组成。</summary>
public sealed record BaiduParagraph
{
    [JsonPropertyName("words_result_idx")] public IReadOnlyList<int>? WordsResultIdx { get; init; }
}

/// <summary>常见错误码 → Iris ErrorCode 映射见 ARCHITECTURE.md §9.3。</summary>
public static class BaiduErrorCodes
{
    public const int NoPermission = 6;
    public const int IamCertificationFailed = 14;
    public const int DailyLimitReached = 17;
    public const int QpsLimitReached = 18;
    public const int TotalLimitReached = 19;
    public const int AccessTokenInvalid = 110;
    public const int AccessTokenExpired = 111;
    public const int InvalidParam = 216100;
    public const int NotEnoughParam = 216101;
    public const int ImageEmpty = 216200;
    public const int ImageFormatError = 216201;
    public const int ImageSizeError = 216202;
    public const int RequestBodyTooLarge = 216205;
    public const int RecognizeError = 216630;
    public const int InternalError = 282000;
}
