using System;
using Iris.Core.Common;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class ProviderCredentialResolverTests
{
    private readonly ISecretStore _secretStore = Substitute.For<ISecretStore>();

    [Fact]
    public void Resolve_WhenCredentialSetIdIsNull_AndBothKeysPresent_ReturnsCredentials()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-1",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = null
        };

        _secretStore.Get(SecretKeys.ApiKey("tp-1")).Returns("api-key-1");
        _secretStore.Get(SecretKeys.SecretKey("tp-1")).Returns("secret-key-1");

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldNotBeNull();
        result.Credentials!.ApiKey.ShouldBe("api-key-1");
        result.Credentials.SecretKey.ShouldBe("secret-key-1");
    }

    [Fact]
    public void Resolve_WhenCredentialSetIdIsNull_AndOnlyApiKeyPresent_ReturnsCredentials()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-1",
            Kind = TranslationProviderKind.Anthropic,
            DisplayName = "Test",
            Model = "claude-sonnet-5",
            CredentialSetId = null
        };

        _secretStore.Get(SecretKeys.ApiKey("tp-1")).Returns("sk-ant-123");
        _secretStore.Get(SecretKeys.SecretKey("tp-1")).Returns((string?)null);

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldNotBeNull();
        result.Credentials!.ApiKey.ShouldBe("sk-ant-123");
        result.Credentials.SecretKey.ShouldBeNull();
    }

    [Fact]
    public void Resolve_WhenCredentialSetIdIsNull_AndBothKeysMissing_ReturnsNone()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-local",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Local Ollama",
            Model = "qwen2.5",
            CredentialSetId = null
        };

        _secretStore.Get(SecretKeys.ApiKey("tp-local")).Returns((string?)null);
        _secretStore.Get(SecretKeys.SecretKey("tp-local")).Returns("");

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldBe(ProviderCredentials.None);
    }

    [Fact]
    public void Resolve_WhenCredentialSetIdIsValid_AndJsonValid_ReturnsCredentials()
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-valid",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        var json = """
        {
          "format": "iris-provider-credentials",
          "version": 1,
          "apiKey": "my-api-key",
          "secretKey": "my-secret-key"
        }
        """;
        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns(json);

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldNotBeNull();
        result.Credentials!.ApiKey.ShouldBe("my-api-key");
        result.Credentials.SecretKey.ShouldBe("my-secret-key");
    }

    [Fact]
    public void Resolve_WhenCredentialSetIdIsValid_AndBothKeysNullInJson_ReturnsNone()
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-nulls",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        var json = """
        {
          "format": "iris-provider-credentials",
          "version": 1,
          "apiKey": null,
          "secretKey": null
        }
        """;
        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns(json);

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldBe(ProviderCredentials.None);
    }

    [Fact]
    public void Resolve_WhenCredentialSetIdIsValid_AndExtraFieldPresent_IgnoresExtraAndSucceeds()
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-extra",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        var json = """
        {
          "format": "iris-provider-credentials",
          "version": 1,
          "apiKey": "k",
          "secretKey": null,
          "extraInfo": { "notes": "ignore me", "flag": 123 }
        }
        """;
        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns(json);

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldNotBeNull();
        result.Credentials!.ApiKey.ShouldBe("k");
        result.Credentials.SecretKey.ShouldBeNull();
    }

    [Theory]
    [InlineData("cs-123")] // Too short
    [InlineData("cs-0123456789abcdef0123456789ABCDEF")] // Contains uppercase
    [InlineData("cs-01234567-89ab-cdef-0123-456789abcdef")] // Contains hyphens
    [InlineData("0123456789abcdef0123456789abcdef")] // Missing cs- prefix
    [InlineData("cs-xyz0123456789abcdef0123456789ab")] // Non-hex chars
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WhenCredentialSetIdIsInvalidFormat_ReturnsExplicitUnconfigured_DoesNotFallbackToLegacy(string invalidId)
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-bad-format",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = invalidId
        };

        // Legacy keys exist, but must NOT be accessed or used
        _secretStore.Get(SecretKeys.ApiKey("tp-bad-format")).Returns("legacy-key");

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeTrue();
        result.Credentials.ShouldBeNull();
        _secretStore.DidNotReceive().Get(SecretKeys.ApiKey("tp-bad-format"));
    }

    [Fact]
    public void Resolve_WhenCredentialSetNotFound_ReturnsExplicitUnconfigured_DoesNotFallbackToLegacy()
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-missing-set",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns((string?)null);
        _secretStore.Get(SecretKeys.ApiKey("tp-missing-set")).Returns("legacy-key");

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeTrue();
        result.Credentials.ShouldBeNull();
        _secretStore.DidNotReceive().Get(SecretKeys.ApiKey("tp-missing-set"));
    }

    [Theory]
    [InlineData("""{"format":"wrong","version":1,"apiKey":"k","secretKey":null}""")] // Wrong format
    [InlineData("""{"Format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null}""")] // Case sensitive
    [InlineData("""{"format":"iris-provider-credentials","version":2,"apiKey":"k","secretKey":null}""")] // Unknown version
    [InlineData("""{"format":"iris-provider-credentials","version":0,"apiKey":"k","secretKey":null}""")] // Version 0
    [InlineData("""{"version":1,"apiKey":"k","secretKey":null}""")] // Missing format
    [InlineData("""{"format":"iris-provider-credentials","apiKey":"k","secretKey":null}""")] // Missing version
    [InlineData("""{"format":"iris-provider-credentials","version":1,"secretKey":null}""")] // Missing apiKey
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k"}""")] // Missing secretKey
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":123,"secretKey":null}""")] // apiKey is number
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":false}""")] // secretKey is bool
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null,"apiKey":"dup"}""")] // Duplicate property
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null,"format":"dup"}""")] // Duplicate format
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null,"nested":{"dup":1,"dup":2}}""")] // Nested duplicate in object
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null,"arr":[{"dup":1,"dup":2}]}""")] // Nested duplicate in object within array
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null,"nested":{"inner":{"dup":1,"dup":2}}}""")] // Deeply nested duplicate
    [InlineData("""{"format":"iris-provider-credentials","version":1,"apiKey":"k","secretKey":null} trailing-junk""")] // Trailing junk
    [InlineData("""not json at all""")]
    [InlineData("""{ broken json""")]
    [InlineData("""["array", "not", "object"]""")]
    public void Resolve_WhenJsonCorruptedOrInvalid_ReturnsExplicitUnconfigured_DoesNotFallbackToLegacy(string invalidJson)
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-bad-json",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns(invalidJson);
        _secretStore.Get(SecretKeys.ApiKey("tp-bad-json")).Returns("legacy-key");

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeTrue();
        result.Credentials.ShouldBeNull();
        _secretStore.DidNotReceive().Get(SecretKeys.ApiKey("tp-bad-json"));
    }

    [Theory]
    [InlineData("cs-0123456789abcdef0123456789abcdef\n")]
    [InlineData("cs-0123456789abcdef0123456789abcdef\r\n")]
    [InlineData("\ncs-0123456789abcdef0123456789abcdef")]
    [InlineData("cs-0123456789abcdef0123456789abcdef ")]
    [InlineData(" cs-0123456789abcdef0123456789abcdef")]
    public void Resolve_WhenCredentialSetIdHasTrailingOrLeadingWhitespaceOrNewline_ReturnsExplicitUnconfigured_DoesNotQuerySecretStore(string invalidId)
    {
        var profile = new TranslationProviderProfile
        {
            Id = "tp-bad-id",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = invalidId
        };

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeTrue();
        result.Credentials.ShouldBeNull();
        _secretStore.DidNotReceiveWithAnyArgs().Get(default!);
    }

    [Fact]
    public void Resolve_WhenJsonContainsValidNestedObjectsAndArraysWithoutDuplicates_Succeeds()
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-nested-ok",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        var json = """
        {
          "format": "iris-provider-credentials",
          "version": 1,
          "apiKey": "nested-ok-key",
          "secretKey": "nested-ok-secret",
          "metadata": {
            "environment": "production",
            "tags": ["cloud", "azure"]
          },
          "nestedArray": [
            { "item": 1 },
            { "item": 2 }
          ]
        }
        """;

        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns(json);

        var result = ProviderCredentialResolver.Resolve(profile, _secretStore);

        result.IsExplicitUnconfigured.ShouldBeFalse();
        result.Credentials.ShouldNotBeNull();
        result.Credentials!.ApiKey.ShouldBe("nested-ok-key");
        result.Credentials.SecretKey.ShouldBe("nested-ok-secret");
    }

    [Fact]
    public void Resolve_NeverCallsSetOrRemoveOnSecretStore()
    {
        var validSetId = "cs-" + Guid.NewGuid().ToString("N");
        var profile = new TranslationProviderProfile
        {
            Id = "tp-read-only",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test",
            Model = "gpt-4o",
            CredentialSetId = validSetId
        };

        _secretStore.Get(SecretKeys.CredentialSet(validSetId)).Returns((string?)null);

        ProviderCredentialResolver.Resolve(profile, _secretStore);

        _secretStore.DidNotReceiveWithAnyArgs().Set(default!, default!);
        _secretStore.DidNotReceiveWithAnyArgs().Remove(default!);
    }
}
