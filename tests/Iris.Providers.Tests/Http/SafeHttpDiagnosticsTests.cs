using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Iris.Providers.Http;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public class SafeHttpDiagnosticsTests
{
    [Theory]
    [InlineData("https://alice:PASS@api.example/v1?access_token=TOKEN#FRAGMENT", "https://api.example/v1")]
    [InlineData("http://u:p@[::1]:8080/v1/a%20b?q=SECRET", "http://[::1]:8080/v1/a%20b")]
    [InlineData("https://api.example", "https://api.example/")]
    [InlineData("socks5://u:p%40ss@proxy.example:1080", "socks5://proxy.example:1080/")]
    [InlineData("http://api.example:80/v1", "http://api.example/v1")]
    [InlineData("https://api.example:443/v1", "https://api.example/v1")]
    [InlineData("https://api.example:8443/v1/chat", "https://api.example:8443/v1/chat")]
    [InlineData("https://api.example/v1/path%20with%20spaces/", "https://api.example/v1/path%20with%20spaces/")]
    public void ToSafeUrl_ValidUris_ReturnsExpectedSafeUrl(string input, string expected)
    {
        var uri = new Uri(input);

        SafeHttpDiagnostics.ToSafeUrl(uri).ShouldBe(expected);
        SafeHttpDiagnostics.ToSafeUrl(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/relative/path")]
    [InlineData("relative/path?key=secret")]
    [InlineData("not a valid url ://")]
    public void ToSafeUrl_InvalidOrRelative_ReturnsPlaceholder(string? input)
    {
        SafeHttpDiagnostics.ToSafeUrl(input).ShouldBe(SafeHttpDiagnostics.RedactedUrlPlaceholder);
    }

    [Fact]
    public void ToSafeUrl_NullUri_ReturnsPlaceholder()
    {
        SafeHttpDiagnostics.ToSafeUrl((Uri?)null).ShouldBe(SafeHttpDiagnostics.RedactedUrlPlaceholder);
    }

    [Fact]
    public void CleanUrlsInText_RemovesUserInfoQueryAndFragment_PreservesPunctuation()
    {
        var text = "Failed to connect to https://user:secret@api.example.com:8443/v1/chat?token=supersecret#top, check proxy http://u:p@127.0.0.1:8080/proxy?x=1; see also (https://doc.example.com/guide?id=42).";

        var cleaned = SafeHttpDiagnostics.CleanUrlsInText(text);

        cleaned.ShouldBe("Failed to connect to https://api.example.com:8443/v1/chat, check proxy http://127.0.0.1:8080/proxy; see also (https://doc.example.com/guide).");
        cleaned.ShouldNotContain("secret");
        cleaned.ShouldNotContain("supersecret");
        cleaned.ShouldNotContain("user:");
        cleaned.ShouldNotContain("u:p@");
        cleaned.ShouldNotContain("token=");
        cleaned.ShouldNotContain("?x=1");
        cleaned.ShouldNotContain("#top");
        cleaned.ShouldNotContain("?id=42");
    }

    [Theory]
    [InlineData("http://audit:AUDIT_FAKE_PASSWORD_67890@[::1]", "http://[::1]/")]
    [InlineData("https://audit:AUDIT_FAKE_PASSWORD_67890@proxy.invalid:bad/v1?q=AUDIT_FAKE_QUERY_12345", "<redacted-url>")]
    [InlineData("https://api.example/v1?q=left'AUDIT_FAKE_QUERY_12345", "https://api.example/v1")]
    [InlineData("https://api.example/O'Brien?q=AUDIT_FAKE_QUERY_12345", "https://api.example/O'Brien")]
    public void CleanUrlsInText_ProbeCounterExamples_DoNotLeakCredentials(string inputUrl, string expectedCleanedUrl)
    {
        var text = $"Error from upstream: {inputUrl} failed.";

        var cleaned = SafeHttpDiagnostics.CleanUrlsInText(text);

        cleaned.ShouldBe($"Error from upstream: {expectedCleanedUrl} failed.");
        cleaned.ShouldNotContain("AUDIT_FAKE_PASSWORD_67890");
        cleaned.ShouldNotContain("AUDIT_FAKE_QUERY_12345");
    }

    [Fact]
    public void CleanUrlsInText_EnclosedInSingleQuotesAndBrackets_PreservesEnclosureWithoutLeaking()
    {
        var text1 = "Failed at 'http://audit:AUDIT_FAKE_PASSWORD_67890@[::1]'!";
        var cleaned1 = SafeHttpDiagnostics.CleanUrlsInText(text1);
        cleaned1.ShouldBe("Failed at 'http://[::1]/'!");
        cleaned1.ShouldNotContain("AUDIT_FAKE_PASSWORD_67890");

        var text2 = "Check 'https://audit:AUDIT_FAKE_PASSWORD_67890@proxy.invalid:bad/v1?q=AUDIT_FAKE_QUERY_12345'.";
        var cleaned2 = SafeHttpDiagnostics.CleanUrlsInText(text2);
        cleaned2.ShouldBe("Check '<redacted-url>'.");
        cleaned2.ShouldNotContain("AUDIT_FAKE_PASSWORD_67890");
        cleaned2.ShouldNotContain("AUDIT_FAKE_QUERY_12345");

        var text3 = "[http://audit:AUDIT_FAKE_PASSWORD_67890@[::1]]";
        var cleaned3 = SafeHttpDiagnostics.CleanUrlsInText(text3);
        cleaned3.ShouldBe("[http://[::1]/]");

        var text4 = "(https://audit:AUDIT_FAKE_PASSWORD_67890@proxy.invalid:bad/v1?q=AUDIT_FAKE_QUERY_12345)";
        var cleaned4 = SafeHttpDiagnostics.CleanUrlsInText(text4);
        cleaned4.ShouldBe("(<redacted-url>)");
    }

    [Fact]
    public void FormatHttpRequestExceptionDetail_FormatsMetadataWithoutLeakingMessage()
    {
        var sensitiveMessage = "Connection failed to https://proxy.internal:8080 with password supersecretpassword";
        var ex = new HttpRequestException(HttpRequestError.SecureConnectionError, sensitiveMessage, null, HttpStatusCode.BadGateway)
        {
            HResult = unchecked((int)0x80004005)
        };

        var detail = SafeHttpDiagnostics.FormatHttpRequestExceptionDetail(ex);

        detail.ShouldStartWith("网络请求失败（HttpRequestException");
        detail.ShouldContain("HttpRequestError=SecureConnectionError");
        detail.ShouldContain("Status=502");
        detail.ShouldContain("HResult=0x80004005");
        detail.ShouldNotContain("supersecretpassword");
        detail.ShouldNotContain("Connection failed");
    }

    [Fact]
    public void FormatIOExceptionDetail_FormatsMetadataWithoutLeakingMessage()
    {
        var sensitiveMessage = "I/O error during SSL handshake with token=XYZ123456789";
        var ex = new IOException(sensitiveMessage, unchecked((int)0x80070020));

        var detail = SafeHttpDiagnostics.FormatIOExceptionDetail(ex);

        detail.ShouldBe("网络传输读写失败（IOException; HResult=0x80070020）");
        detail.ShouldNotContain("XYZ123456789");
        detail.ShouldNotContain("SSL handshake");
    }

    [Fact]
    public void FormatJsonExceptionDetail_WithLineNumberAndPosition_FormatsLineNumberAndBytePosition()
    {
        var sensitiveMessage = "Invalid character 'X' at line 3, byte 12 in payload: {\"key\":\"secret_value_123\"}";
        var ex = new JsonException(sensitiveMessage, "sensitive.path.here", 3, 12);

        var detail = SafeHttpDiagnostics.FormatJsonExceptionDetail(ex);

        detail.ShouldBe("JSON 解析失败（JsonException; LineNumber=3; BytePosition=12）");
        detail.ShouldNotContain("secret_value_123");
        detail.ShouldNotContain("sensitive.path.here");
    }

    [Fact]
    public void FormatJsonExceptionDetail_WithoutLineNumber_FormatsHResult()
    {
        var sensitiveMessage = "Corrupted JSON stream with token bearer-top-secret";
        var ex = new JsonException(sensitiveMessage)
        {
            HResult = unchecked((int)0x80131500)
        };

        var detail = SafeHttpDiagnostics.FormatJsonExceptionDetail(ex);

        detail.ShouldBe("JSON 解析失败（JsonException; HResult=0x80131500）");
        detail.ShouldNotContain("bearer-top-secret");
    }
}
