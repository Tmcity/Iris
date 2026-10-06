using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Providers.Ocr.Baidu.Wire;

namespace Iris.Providers.Ocr.Baidu;

internal sealed class BaiduOcrProviderFactory(IHttpClientFactory clients, TimeProvider time) : IOcrProviderFactory
{
    private readonly IHttpClientFactory _clients = clients ?? throw new ArgumentNullException(nameof(clients));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    public OcrProviderKind Kind => OcrProviderKind.BaiduCloud;
    public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(credentials);
        if (profile.Kind != Kind) throw new ArgumentException("Incorrect OCR provider kind.", nameof(profile));
        return new BaiduOcrProvider(new BaiduConfiguration(profile, credentials), _clients, _time);
    }
}

internal sealed class BaiduConfiguration
{
    internal ProviderDescriptor Descriptor { get; }
    internal ImageConstraints Constraints { get; }
    internal BaiduOcrOptions Options { get; }
    internal TimeSpan Timeout { get; }
    internal int BodyLimit { get; }
    internal bool Positions => Options.Mode is BaiduOcrMode.General or BaiduOcrMode.Accurate;
    private readonly string? _baseUrl, _apiKey, _secretKey;
    private readonly string _path;
    internal BaiduConfiguration(OcrProviderProfile profile, ProviderCredentials credentials)
    {
        Options = profile.BaiduOptions is { } options ? options with { } : new();
        if (string.IsNullOrWhiteSpace(profile.Id) || !Enum.IsDefined(Options.Mode) || profile.TimeoutSeconds is < 5 or > 120)
            throw new BaiduException(ErrorCode.InvalidArgument, "CONFIGURATION", profile.Id);
        Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
        bool accurate = Options.Mode is BaiduOcrMode.Accurate or BaiduOcrMode.AccurateBasic;
        BodyLimit = accurate ? 10_000_000 : 8_000_000;
        _path = Options.Mode switch
        {
            BaiduOcrMode.GeneralBasic => BaiduEndpoints.GeneralBasicPath, BaiduOcrMode.General => BaiduEndpoints.GeneralPath,
            BaiduOcrMode.Accurate => BaiduEndpoints.AccuratePath, _ => BaiduEndpoints.AccurateBasicPath
        };
        bool urlValid = Uri.TryCreate(Options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
            uri.UserInfo.Length == 0 && !Options.BaseUrl.Contains('?') && !Options.BaseUrl.Contains('#');
        _baseUrl = urlValid ? uri!.AbsoluteUri.TrimEnd('/') : null;
        _apiKey = credentials.ApiKey; _secretKey = credentials.SecretKey;
        bool configured = urlValid && !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_secretKey);
        Descriptor = new(profile.Id, string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName, nameof(OcrProviderKind.BaiduCloud), null, configured);
        string worstLanguage = string.IsNullOrWhiteSpace(Options.LanguageType) ? accurate ? "auto_detect" : "CHN_ENG" : Options.LanguageType;
        if (worstLanguage.Length >= BodyLimit) throw new BaiduException(ErrorCode.InvalidArgument, "FORM_METADATA", profile.Id);
        using var metadata = Form(string.Empty, worstLanguage);
        long overhead = metadata.Headers.ContentLength!.Value;
        if (overhead >= BodyLimit) throw new BaiduException(ErrorCode.InvalidArgument, "FORM_METADATA", profile.Id);
        Constraints = new(4 * 1024 * 1024, true, accurate ? 8192 : 4096, 15,
            Array.AsReadOnly(new[] { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Bmp })) { MaxFormUrlEncodedBytes = BodyLimit - overhead };
    }
    internal string Language(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(Options.LanguageType)) return Options.LanguageType;
        return hint?.ToLowerInvariant() switch
        {
            "zh-hans" or "zh-hant" => "CHN_ENG", "en" => "ENG", "ja" => "JAP", "ko" => "KOR", "fr" => "FRE",
            "de" => "GER", "es" => "SPA", "pt" => "POR", "it" => "ITA", "ru" => "RUS",
            _ => Options.Mode is BaiduOcrMode.Accurate or BaiduOcrMode.AccurateBasic ? "auto_detect" : "CHN_ENG"
        };
    }
    internal Uri TokenUri() => new(_baseUrl + BaiduEndpoints.TokenPath + "?grant_type=client_credentials&client_id=" +
        Uri.EscapeDataString(_apiKey!) + "&client_secret=" + Uri.EscapeDataString(_secretKey!));
    internal Uri RecognitionUri(string token) => new(_baseUrl + _path + "?access_token=" + Uri.EscapeDataString(token));
    internal FormUrlEncodedContent Form(string image, string language) => new(new Dictionary<string, string>
    {
        [BaiduFormFields.Image] = image, [BaiduFormFields.LanguageType] = language,
        [BaiduFormFields.DetectDirection] = Options.DetectDirection ? "true" : "false",
        [BaiduFormFields.Paragraph] = Options.Paragraph ? "true" : "false", [BaiduFormFields.Probability] = "true"
    });
    internal void ValidateImage(ImageData? image)
    {
        if (image?.Bytes == null || image.Bytes.Length == 0 || !Enum.IsDefined(image.Format) || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new BaiduException(ErrorCode.ImageInvalid, "IMAGE", Descriptor.Id);
        if (Math.Max(image.PixelWidth, image.PixelHeight) > Constraints.MaxLongSide || Math.Min(image.PixelWidth, image.PixelHeight) < Constraints.MinShortSide ||
            4 * ((image.Bytes.LongLength + 2) / 3) > Constraints.MaxEncodedBytes)
            throw new BaiduException(ErrorCode.ImageTooLarge, "IMAGE_CONSTRAINTS", Descriptor.Id);
    }
}
