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
    public void Redact_KnownSecretsShorterThanEightCharacters_AreNotReplaced()
    {
        var secrets = new List<string?> { "short", "1234567" };
        var text = "Values short and 1234567 should stay.";

        var redacted = SecretRedactor.Redact(text, secrets);

        redacted.ShouldBe("Values short and 1234567 should stay.");
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
        var text1 = "http://api.example.com?access_token=24.abc&x=1";
        var redacted1 = SecretRedactor.Redact(text1);
        redacted1.ShouldBe("http://api.example.com?access_token=***&x=1");

        var text2 = "Failed with api_key=secret-key-12345 and secret_key=secret-val-9876";
        var redacted2 = SecretRedactor.Redact(text2);
        redacted2.ShouldBe("Failed with api_key=*** and secret_key=***");
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
}
