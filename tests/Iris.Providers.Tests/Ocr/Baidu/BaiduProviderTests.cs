using System.Net;
using System.Text.Json;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Providers.Ocr.Baidu;
using Iris.Providers.Ocr.Baidu.Wire;
using Shouldly;
using static Iris.Providers.Tests.Ocr.Baidu.BaiduTestScope;

namespace Iris.Providers.Tests.Ocr.Baidu;

public sealed class BaiduProviderTests
{
    [Theory]
    [InlineData(BaiduOcrMode.GeneralBasic, false)][InlineData(BaiduOcrMode.AccurateBasic, true)]
    public Task ActualBodyBetweenModeLimits_RespectsDecimalBudget(BaiduOcrMode mode, bool accepted) => Run(async f =>
    {
        var bytes = new byte[2250000]; bytes.AsSpan().Fill(255);
        var task = f.Recognize(Input(bytes: bytes), accepted ? null : typeof(IrisException));
        if (accepted) { await task; f.Requests.Last().Body!.Length.ShouldBeGreaterThan(8000000); f.Requests.Last().Body!.Length.ShouldBeLessThan(10000000); }
        else { (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.ImageTooLarge); f.Requests.ShouldBeEmpty(); }
    }, Profile(mode));

    [Fact]
    public void MetadataExhaustingFormBudget_IsInvalidWithoutHttp()
    {
        using var f = new BaiduTestScope();
        var factory = new BaiduOcrProviderFactory(f, f.Time);
        var profile = Profile(BaiduOcrMode.GeneralBasic) with { BaiduOptions = new() { Mode = BaiduOcrMode.GeneralBasic, LanguageType = new string('漢', 900000) } };
        Should.Throw<IrisException>(() => factory.Create(profile, new(ApiKey, Secret))).Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Clients.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(BaiduOcrMode.GeneralBasic, "general_basic", 4096, 8000000, "CHN_ENG")]
    [InlineData(BaiduOcrMode.General, "general", 4096, 8000000, "CHN_ENG")]
    [InlineData(BaiduOcrMode.AccurateBasic, "accurate_basic", 8192, 10000000, "auto_detect")]
    [InlineData(BaiduOcrMode.Accurate, "accurate", 8192, 10000000, "auto_detect")]
    public Task Modes_UsePrefixEscapedCredentialsExactFormsAndConstraints(BaiduOcrMode mode, string endpoint, int max, int bodyLimit, string auto) => Run(async f =>
    {
        f.Clients.ShouldBeEmpty();
        var result = await f.Recognize(); result.Lines.ShouldBeEmpty(); result.DetectedLanguage.ShouldBeNull();
        var requests = f.Requests.ToArray(); requests.Length.ShouldBe(2);
        requests[0].Uri.AbsolutePath.ShouldBe("/gateway/v1/oauth/2.0/token");
        Fields(requests[0].Uri.Query[1..])["client_id"].ShouldBe(ApiKey);
        Fields(requests[0].Uri.Query[1..])["client_secret"].ShouldBe(Secret);
        requests[1].Uri.AbsolutePath.ShouldBe("/gateway/v1/rest/2.0/ocr/v1/" + endpoint);
        Fields(requests[1].Uri.Query[1..])["access_token"].ShouldBe(TokenValue);
        var form = Fields(requests[1].Body!);
        form.ShouldBe(new Dictionary<string, string> { ["image"] = "AQID", ["language_type"] = auto, ["detect_direction"] = "true", ["paragraph"] = "true", ["probability"] = "true" });
        requests[1].Type.ShouldBe("application/x-www-form-urlencoded");
        var c = f.Provider.Constraints; c.MaxLongSide.ShouldBe(max); c.MinShortSide.ShouldBe(15); c.MaxEncodedBytes.ShouldBe(4 * 1024 * 1024); c.MaxBytesAppliesToBase64.ShouldBeTrue();
        c.AcceptedFormats.ShouldBe(new[] { Iris.Core.Imaging.ImageFormat.Jpeg, Iris.Core.Imaging.ImageFormat.Png, Iris.Core.Imaging.ImageFormat.Bmp });
        int overhead = requests[1].Body!.Length - Uri.EscapeDataString("AQID").Length;
        c.MaxFormUrlEncodedBytes.ShouldBe(bodyLimit - overhead);
        f.Streams.ShouldAllBe(s => s.Released); f.Clients.ShouldAllBe(c => c.Released);
    }, Profile(mode));

    [Theory]
    [InlineData("zh-Hans", "CHN_ENG")][InlineData("ZH-HANT", "CHN_ENG")][InlineData("EN", "ENG")]
    [InlineData("ja", "JAP")][InlineData("ko", "KOR")][InlineData("fr", "FRE")][InlineData("de", "GER")]
    [InlineData("es", "SPA")][InlineData("pt", "POR")][InlineData("it", "ITA")][InlineData("ru", "RUS")]
    [InlineData("unknown", "auto_detect")][InlineData("", "auto_detect")]
    public Task Languages_MapWithoutFakingDetectedLanguage(string hint, string expected) => Run(async f =>
    {
        (await f.Recognize(Input(hint))).DetectedLanguage.ShouldBeNull();
        Fields(f.Requests.Last().Body!)["language_type"].ShouldBe(expected);
    });

    [Fact]
    public Task ExplicitLanguageAndFlags_ArePreserved() => Run(async f =>
    {
        await f.Recognize(Input("en")); var body = f.Requests.Last().Body!;
        Fields(body)["language_type"].ShouldBe("X+ &漢字"); Fields(body)["detect_direction"].ShouldBe("false"); Fields(body)["paragraph"].ShouldBe("false");
        f.Provider.Constraints.MaxFormUrlEncodedBytes.ShouldBe(10000000 - (body.Length - 4));
    }, Profile() with { BaiduOptions = new() { LanguageType = "X+ &漢字", DetectDirection = false, Paragraph = false } });

    [Theory]
    [InlineData("https://example.test/?q=x", "key", "secret")][InlineData("https://u:p@example.test", "key", "secret")]
    [InlineData("ftp://example.test", "key", "secret")][InlineData("https://example.test/#x", "key", "secret")]
    [InlineData("https://example.test", " ", "secret")][InlineData("https://example.test", "key", "")]
    public Task Unconfigured_IsLazyAndReturnsSafeFailures(string url, string key, string secret) => Run(async f =>
    {
        f.Provider.Descriptor.IsConfigured.ShouldBeFalse();
        (await f.Provider.TestAsync(default)).Error!.Code.ShouldBe(ErrorCode.NotConfigured);
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.NotConfigured);
        f.Clients.ShouldBeEmpty(); f.Requests.ShouldBeEmpty();
    }, Profile() with { BaiduOptions = new() { BaseUrl = url } }, new(key, secret));

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void InvalidProfileAndFactoryArguments_FailSafely(int kind)
    {
        using var f = new BaiduTestScope();
        var factory = new BaiduOcrProviderFactory(f, f.Time);
        var profile = kind switch { 0 => Profile() with { Id = "" }, 1 => Profile() with { TimeoutSeconds = 4 }, 2 => Profile() with { TimeoutSeconds = 121 }, _ => Profile() with { BaiduOptions = new() { Mode = (BaiduOcrMode)99 } } };
        Should.Throw<IrisException>(() => factory.Create(profile, new(ApiKey, Secret))).Code.ShouldBe(ErrorCode.InvalidArgument);
        Should.Throw<ArgumentNullException>(() => factory.Create(null!, new(ApiKey, Secret)));
        Should.Throw<ArgumentException>(() => factory.Create(Profile() with { Kind = OcrProviderKind.WindowsLocal }, new(ApiKey, Secret)));
    }

    [Fact]
    public Task RealFullBodyBudget_RejectsDenseEncodingBeforeAnyHttp() => Run(async f =>
    {
        var bytes = new byte[3 * 1024 * 1024]; bytes.AsSpan().Fill(255);
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(Input(bytes: bytes), typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Requests.ShouldBeEmpty(); f.Clients.ShouldBeEmpty();
        bytes.AsSpan().Clear(); await f.Recognize(Input(bytes: bytes));
        f.Requests.Last().Body!.Length.ShouldBeLessThan(8000000);
    }, Profile(BaiduOcrMode.GeneralBasic));

    [Theory]
    [InlineData(14, ErrorCode.AuthenticationFailed, false)][InlineData(110, ErrorCode.AuthenticationFailed, false)][InlineData(111, ErrorCode.AuthenticationFailed, false)]
    [InlineData(6, ErrorCode.PermissionDenied, false)][InlineData(17, ErrorCode.QuotaExceeded, false)][InlineData(19, ErrorCode.QuotaExceeded, false)]
    [InlineData(18, ErrorCode.RateLimited, true)][InlineData(216200, ErrorCode.ImageInvalid, false)][InlineData(216201, ErrorCode.ImageInvalid, false)]
    [InlineData(216202, ErrorCode.ImageTooLarge, false)][InlineData(216205, ErrorCode.ImageTooLarge, false)]
    [InlineData(216100, ErrorCode.BadRequest, false)][InlineData(216101, ErrorCode.BadRequest, false)]
    [InlineData(216630, ErrorCode.ProviderUnavailable, true)][InlineData(282000, ErrorCode.ProviderUnavailable, true)][InlineData(999, ErrorCode.Unknown, false)]
    public Task BusinessErrors_AreBoundedAndNeverExposeText(int code, ErrorCode expected, bool retryable) => Run(async f =>
    {
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json($"{{\"error_code\":{code},\"error_msg\":\"private-server-error\",\"words_result\":false}}"));
        var task = f.Recognize(expected: typeof(IrisException));
        if (code == 18) { await f.Time.TimerCreated(TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(10)); f.Time.Advance(TimeSpan.FromMilliseconds(500)); }
        var error = await Should.ThrowAsync<IrisException>(() => task);
        error.Code.ShouldBe(expected); error.Retryable.ShouldBe(retryable); error.Detail!.ShouldNotContain("private-server-error"); error.InnerException.ShouldBeNull();
        f.Requests.Count(r => !r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).ShouldBe(code is 110 or 111 or 18 ? 2 : 1);
    });

    [Theory]
    [InlineData(301, ErrorCode.InvalidResponse)][InlineData(400, ErrorCode.BadRequest)][InlineData(401, ErrorCode.AuthenticationFailed)]
    [InlineData(403, ErrorCode.PermissionDenied)][InlineData(404, ErrorCode.EndpointNotFound)][InlineData(408, ErrorCode.Timeout)]
    [InlineData(413, ErrorCode.ImageTooLarge)][InlineData(429, ErrorCode.RateLimited)][InlineData(500, ErrorCode.ProviderUnavailable)][InlineData(504, ErrorCode.Timeout)]
    public Task HttpErrors_DoNotEnterBusinessRetry(int status, ErrorCode expected) => Run(async f =>
    {
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json("{\"error_code\":18,\"error_msg\":\"private\"}", (HttpStatusCode)status));
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)))).Code.ShouldBe(expected);
        f.Requests.Count.ShouldBe(2);
    });

    [Fact]
    public void WireDiagnostics_HideSensitiveFields()
    {
        var token = new BaiduTokenResponse { AccessToken = TokenValue, ErrorDescription = Secret, Scope = ApiKey, Error = "private-error" };
        var response = new BaiduOcrResponse { ErrorMsg = Secret, WordsResult = [new() { Words = ApiKey }] };
        foreach (string secret in new[] { ApiKey, Secret, TokenValue, "private-error" }) { token.ToString().ShouldNotContain(secret); response.ToString().ShouldNotContain(secret); }
        JsonSerializer.Serialize(token).ShouldContain("access_token"); BaiduErrorCodes.RequestBodyTooLarge.ShouldBe(216205);
    }
    internal static Dictionary<string, string> Fields(string text) => text.Split('&').ToDictionary(p => Uri.UnescapeDataString(p.Split('=', 2)[0]),
        p => Uri.UnescapeDataString(p.Split('=', 2)[1].Replace('+', ' ')), StringComparer.Ordinal);
}
