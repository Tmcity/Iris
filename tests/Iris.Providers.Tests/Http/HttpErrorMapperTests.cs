using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Providers.Http;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public sealed class HttpErrorMapperTests
{
    [Theory]
    [InlineData(301, null, ErrorCode.InvalidResponse, false)]
    [InlineData(302, null, ErrorCode.InvalidResponse, false)]
    [InlineData(400, null, ErrorCode.BadRequest, false)]
    [InlineData(422, null, ErrorCode.BadRequest, false)]
    [InlineData(401, null, ErrorCode.AuthenticationFailed, false)]
    [InlineData(402, null, ErrorCode.QuotaExceeded, false)]
    [InlineData(403, null, ErrorCode.PermissionDenied, false)]
    [InlineData(404, null, ErrorCode.EndpointNotFound, false)]
    [InlineData(408, null, ErrorCode.Timeout, true)]
    [InlineData(504, null, ErrorCode.Timeout, true)]
    [InlineData(413, null, ErrorCode.InputTooLong, false)]
    [InlineData(418, null, ErrorCode.BadRequest, false)]
    [InlineData(429, null, ErrorCode.RateLimited, true)]
    [InlineData(429, "other_error", ErrorCode.RateLimited, true)]
    [InlineData(429, "insufficient_quota", ErrorCode.QuotaExceeded, false)]
    [InlineData(429, "INSUFFICIENT_QUOTA", ErrorCode.QuotaExceeded, false)]
    [InlineData(500, null, ErrorCode.ProviderUnavailable, true)]
    [InlineData(502, null, ErrorCode.ProviderUnavailable, true)]
    [InlineData(503, null, ErrorCode.ProviderUnavailable, true)]
    [InlineData(529, null, ErrorCode.ProviderUnavailable, true)]
    [InlineData(599, null, ErrorCode.ProviderUnavailable, true)]
    public void MapStatus_MapsExpectedCodesAndRetryability(
        int statusCode,
        string? providerErrorCode,
        ErrorCode expectedCode,
        bool expectedRetryable)
    {
        var (code, retryable) = HttpErrorMapper.MapStatus(statusCode, providerErrorCode);

        code.ShouldBe(expectedCode);
        retryable.ShouldBe(expectedRetryable);
    }

    [Fact]
    public void FromBody_OpenAiErrorFormat_ExtractsMessageAndDetail()
    {
        const string body = "{\"error\":{\"message\":\"Incorrect API key\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}";
        var ex = HttpErrorMapper.FromBody(401, body, "openai", null);

        ex.Code.ShouldBe(ErrorCode.AuthenticationFailed);
        ex.Detail.ShouldBe("HTTP 401: Incorrect API key");
        ex.HttpStatus.ShouldBe(401);
        ex.ProviderId.ShouldBe("openai");
    }

    [Fact]
    public void FromBody_AnthropicErrorFormat_ExtractsMessageAndDetail()
    {
        const string body = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}";
        var ex = HttpErrorMapper.FromBody(529, body, "anthropic", null);

        ex.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        ex.Detail.ShouldBe("HTTP 529: Overloaded");
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public void FromBody_OllamaErrorStringFormat_ExtractsMessage()
    {
        const string body = "{\"error\":\"model 'llama' not found\"}";
        var ex = HttpErrorMapper.FromBody(404, body, "ollama", null);

        ex.Code.ShouldBe(ErrorCode.EndpointNotFound);
        ex.Detail.ShouldBe("HTTP 404: model 'llama' not found");
    }

    [Fact]
    public void FromBody_DetailFormat_ExtractsMessage()
    {
        const string body = "{\"detail\":\"Not Found Here\"}";
        var ex = HttpErrorMapper.FromBody(404, body, "test", null);

        ex.Code.ShouldBe(ErrorCode.EndpointNotFound);
        ex.Detail.ShouldBe("HTTP 404: Not Found Here");
    }

    [Fact]
    public void FromBody_BaiduErrorMsgFormat_ExtractsMessage()
    {
        const string body = "{\"error_code\":110,\"error_msg\":\"Access token invalid\"}";
        var ex = HttpErrorMapper.FromBody(200, body, "baidu", null);

        ex.Detail.ShouldBe("HTTP 200: Access token invalid");
    }

    [Fact]
    public void FromBody_HtmlFormat_CompressesWhitespace()
    {
        const string body = "<html>\n  <head><title>502</title></head>\n  <body>\n    Bad Gateway\n  </body>\n</html>";
        var ex = HttpErrorMapper.FromBody(502, body, "gateway", null);

        ex.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        ex.Detail.ShouldBe("HTTP 502: <html> <head><title>502</title></head> <body> Bad Gateway </body> </html>");
    }

    [Fact]
    public void FromBody_EmptyBody_FormatsDetailWithStatusOnly()
    {
        var ex1 = HttpErrorMapper.FromBody(500, "", "test", null);
        ex1.Detail.ShouldBe("HTTP 500");

        var ex2 = HttpErrorMapper.FromBody(503, "   ", "test", null);
        ex2.Detail.ShouldBe("HTTP 503");
    }

    [Fact]
    public void FromBody_NumericCodeWithInsufficientQuotaType_MapsToQuotaExceeded()
    {
        const string body = "{\"error\":{\"code\":429,\"type\":\"insufficient_quota\",\"message\":\"You exceeded your current quota\"}}";
        var ex = HttpErrorMapper.FromBody(429, body, "openai", null);

        ex.Code.ShouldBe(ErrorCode.QuotaExceeded);
        ex.Retryable.ShouldBeFalse();
        ex.Detail.ShouldBe("HTTP 429: You exceeded your current quota");
    }

    [Fact]
    public void FromBody_TruncatesToThreeHundredRunesWithEllipsis()
    {
        var longMessage = new string('A', 400);
        var body = JsonSerializer.Serialize(new { message = longMessage });

        var ex = HttpErrorMapper.FromBody(400, body, "test", null);

        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldBe($"HTTP 400: {new string('A', 300)}…");
    }

    [Fact]
    public void FromBody_EmojiAtBoundary_DoesNotSplitSurrogatePair()
    {
        // 299 'A's + emoji (surrogate pair, 2 chars) + more 'B's
        var message = new string('A', 299) + "\U0001F600" + new string('B', 50);
        var body = JsonSerializer.Serialize(new { message });

        var ex = HttpErrorMapper.FromBody(400, body, "test", null);

        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("\U0001F600…");
        ex.Detail.EndsWith('…').ShouldBeTrue();
    }

    [Fact]
    public void FromBody_RedactsKnownSecretsInDetail()
    {
        var secrets = new List<string?> { "secret-key-12345678" };
        var body = "{\"error\":{\"message\":\"Invalid credentials for secret-key-12345678\"}}";

        var ex = HttpErrorMapper.FromBody(401, body, "test", secrets);

        ex.Detail.ShouldBe("HTTP 401: Invalid credentials for ***");
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldNotContain("secret-key-12345678");
    }

    [Fact]
    public async Task FromResponseAsync_LargeResponseBody_ReadsOnlyUpTo64Kb()
    {
        // 1 MB response body
        var oneMbString = new string('x', 1024 * 1024);
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(oneMbString, Encoding.UTF8, "text/plain"),
        };

        var start = Environment.TickCount64;
        var ex = await HttpErrorMapper.FromResponseAsync(response, "test", null, TestContext.Current.CancellationToken);
        var elapsed = Environment.TickCount64 - start;

        elapsed.ShouldBeLessThan(1000);
        ex.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.EndsWith('…').ShouldBeTrue();
    }

    [Fact]
    public void TryMapException_MapsKnownExceptionsCorrectly()
    {
        var httpEx = new HttpRequestException("Connection refused");
        var mappedHttp = HttpErrorMapper.TryMapException(httpEx, "provider1");
        mappedHttp.ShouldNotBeNull();
        mappedHttp.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        mappedHttp.Retryable.ShouldBeTrue();
        mappedHttp.InnerException.ShouldBe(httpEx);

        var ioEx = new IOException("The connection was reset");
        var mappedIo = HttpErrorMapper.TryMapException(ioEx, "provider2");
        mappedIo.ShouldNotBeNull();
        mappedIo.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        mappedIo.Retryable.ShouldBeTrue();
        mappedIo.InnerException.ShouldBe(ioEx);

        var jsonEx = new JsonException("Invalid JSON token");
        var mappedJson = HttpErrorMapper.TryMapException(jsonEx, "provider3");
        mappedJson.ShouldNotBeNull();
        mappedJson.Code.ShouldBe(ErrorCode.InvalidResponse);
        mappedJson.Retryable.ShouldBeFalse();
        mappedJson.InnerException.ShouldBe(jsonEx);

        var cancelEx = new OperationCanceledException();
        HttpErrorMapper.TryMapException(cancelEx, "provider4").ShouldBeNull();

        var invalidOpEx = new InvalidOperationException();
        HttpErrorMapper.TryMapException(invalidOpEx, "provider5").ShouldBeNull();
    }

    [Fact]
    public void Timeout_ConstructsTimeoutIrisExceptionWithSeconds()
    {
        var ex = HttpErrorMapper.Timeout("llm-provider", TimeSpan.FromSeconds(15));

        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
        ex.ProviderId.ShouldBe("llm-provider");
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("15");
    }

    [Fact]
    public void TryMapException_RedactsProxyCredentialsInExceptionDetail()
    {
        var ex = new HttpRequestException("The proxy tunnel request to proxy 'http://alice:S3cretPass@10.0.0.1:8080/' failed");
        var mapped = HttpErrorMapper.TryMapException(ex, "provider-p1");

        mapped.ShouldNotBeNull();
        mapped.Detail.ShouldNotBeNull();
        mapped.Detail.ShouldNotContain("S3cretPass");
        mapped.Detail.ShouldNotContain("alice");
        mapped.Detail.ShouldContain("http://***@10.0.0.1:8080/");
    }
}
