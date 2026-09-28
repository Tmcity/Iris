using System.Collections.Generic;
using Iris.Providers.Http;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_KnownSecrets_ReplacesOccurrencesWithAsterisks()
    {
        var secrets = new List<string?> { "my-secret-key-123", "another-secret-456" };
        var text = "key1=my-secret-key-123 and repeat: my-secret-key-123, key2=another-secret-456";

        var redacted = SecretRedactor.Redact(text, secrets);

        redacted.ShouldBe("key1=*** and repeat: ***, key2=***");
    }

    [Fact]
    public void Redact_KnownSecretsShorterThanEightCharacters_AreReplaced()
    {
        var secrets = new List<string?> { "short", "1234567" };
        var text = "Values short and 1234567 should stay.";

        var redacted = SecretRedactor.Redact(text, secrets);

        redacted.ShouldBe("Values *** and *** should stay.");
    }

    [Fact]
    public void Redact_OverlappingKnownSecrets_LongestMatchTakesPrecedenceWithoutExpansion()
    {
        var secrets = new List<string?> { "secret", "secret_extended", "sec" };
        var text = "text with secret_extended and secret token";

        var redacted = SecretRedactor.Redact(text, secrets);

        redacted.ShouldBe("text with *** and *** token");
    }

    [Fact]
    public void Redact_UrlCleaningBeforeShortSecret_PreventsSchemeCorruptionLeak()
    {
        // If "http" were replaced first, the URL scheme would be broken to "***://..." and query wouldn't be stripped.
        var secrets = new List<string?> { "http" };
        var text = "Error contacting http://example.com/api?secret_param=hidden_value";

        var redacted = SecretRedactor.Redact(text, secrets);

        redacted.ShouldBe("Error contacting ***://example.com/api");
        redacted.ShouldNotContain("secret_param");
        redacted.ShouldNotContain("hidden_value");
    }

    [Fact]
    public void Redact_OpenAiKeyPattern_ReplacesWithSkAsterisks()
    {
        var text1 = "Incorrect API key provided: sk-proj-AbCdEf1234567890";
        var redacted1 = SecretRedactor.Redact(text1);
        redacted1.ShouldBe("Incorrect API key provided: sk-***");

        var text2 = "Failed with key sk-abcdef1234567890_test123 in request";
        var redacted2 = SecretRedactor.Redact(text2);
        redacted2.ShouldBe("Failed with key sk-*** in request");
    }

    [Fact]
    public void Redact_ParameterSecretPattern_ReplacesTokenValue()
    {
        var text1 = "failed auth access_token=24.abc&x=1";
        var redacted1 = SecretRedactor.Redact(text1);
        redacted1.ShouldBe("failed auth access_token=***&x=1");

        var text2 = "Failed with api_key=secret-key-12345 and secret_key=secret-val-9876";
        var redacted2 = SecretRedactor.Redact(text2);
        redacted2.ShouldBe("Failed with api_key=*** and secret_key=***");

        var text3 = "http://api.example.com?access_token=24.abc&x=1";
        var redacted3 = SecretRedactor.Redact(text3);
        redacted3.ShouldBe("http://api.example.com/");
    }

    [Fact]
    public void Redact_BearerPattern_ReplacesToken()
    {
        var text1 = "Authorization: Bearer eyJhbGciOi1234567890";
        var redacted1 = SecretRedactor.Redact(text1);
        redacted1.ShouldBe("Authorization: Bearer ***");

        var text2 = "error header: bearer token_12345678";
        var redacted2 = SecretRedactor.Redact(text2);
        redacted2.ShouldBe("error header: Bearer ***");
    }

    [Fact]
    public void Redact_NullOrEmptyKnownSecretsOrNullElements_DoesNotThrow()
    {
        var text = "Normal text without secrets.";

        SecretRedactor.Redact(text, null).ShouldBe(text);
        SecretRedactor.Redact(text, new List<string?> { null, "valid-secret-token", null }).ShouldBe(text);
        SecretRedactor.Redact(string.Empty, null).ShouldBe(string.Empty);
    }

    [Fact]
    public void Redact_UrlCredentialsPattern_RemovesCredentialsFromUrl()
    {
        var text1 = "The proxy tunnel request to proxy 'http://alice:S3cretPass@127.0.0.1:10332/' failed";
        var redacted1 = SecretRedactor.Redact(text1);
        redacted1.ShouldNotContain("alice");
        redacted1.ShouldNotContain("S3cretPass");
        redacted1.ShouldContain("http://127.0.0.1:10332/");
        redacted1.ShouldNotContain("***@");

        var text2 = "socks5://u:p%40ss@h:1080";
        var redacted2 = SecretRedactor.Redact(text2);
        redacted2.ShouldBe("socks5://h:1080/");
        redacted2.ShouldNotContain("***@");

        var text3 = "https://api.openai.com/v1/chat";
        SecretRedactor.Redact(text3).ShouldBe(text3);

        var text4 = "http://host:8080/path";
        SecretRedactor.Redact(text4).ShouldBe(text4);

        var text5 = "user@host";
        SecretRedactor.Redact(text5).ShouldBe(text5);
    }

    [Theory]
    [InlineData("http://audit:AUDIT_FAKE_PASSWORD_67890@[::1]", "http://[::1]/")]
    [InlineData("https://audit:AUDIT_FAKE_PASSWORD_67890@proxy.invalid:bad/v1?q=AUDIT_FAKE_QUERY_12345", "<redacted-url>")]
    [InlineData("https://api.example/v1?q=left'AUDIT_FAKE_QUERY_12345", "https://api.example/v1")]
    [InlineData("https://api.example/O'Brien?q=AUDIT_FAKE_QUERY_12345", "https://api.example/O'Brien")]
    public void Redact_ProbeCounterExamples_WithUnrelatedKnownSecrets_DoesNotLeak(string url, string expectedCleanedUrl)
    {
        var text = $"Upstream URL: {url}";
        var redacted = SecretRedactor.Redact(text, ["UNRELATED_KEY_123"]);

        redacted.ShouldBe($"Upstream URL: {expectedCleanedUrl}");
        redacted.ShouldNotContain("AUDIT_FAKE_PASSWORD_67890");
        redacted.ShouldNotContain("AUDIT_FAKE_QUERY_12345");
    }

    [Fact]
    public void Redact_HighFrequencyShortSecretRepetition_ReplacesCorrectlyInSinglePass()
    {
        const int count = 4096;
        var input = new string('x', count);
        var redacted = SecretRedactor.Redact(input, ["x"]);

        redacted.Length.ShouldBe(count * 3);
        redacted.ShouldBe(string.Concat(System.Linq.Enumerable.Repeat("***", count)));
    }

    [Fact]
    public void Redact_AdjacentAndMultipleInterspersedKnownSecrets_ReplacesAccurately()
    {
        var secrets = new List<string?> { "foo", "bar", "foobar", "baz" };
        var text = "prefix foobar then foo and bar plus baz suffix";

        var redacted = SecretRedactor.Redact(text, secrets);

        redacted.ShouldBe("prefix *** then *** and *** plus *** suffix");
    }
}
