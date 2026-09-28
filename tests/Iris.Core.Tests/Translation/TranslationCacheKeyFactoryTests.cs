using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Iris.Core.Settings;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Translation;

public sealed class TranslationCacheKeyFactoryTests
{
    private static TranslationProviderRegistration CreateRegistration(string providerId = "test-provider", Guid? instanceId = null)
    {
        var profile = new TranslationProviderProfile
        {
            Id = providerId,
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "Test Provider",
            Model = "gpt-4o",
            BaseUrl = "https://api.example.com",
            TimeoutSeconds = 30,
        };
        var provider = Substitute.For<ITranslationProvider>();
        return new TranslationProviderRegistration(profile, provider, instanceId ?? Guid.NewGuid());
    }

    private static ProviderTranslateRequest CreateRequest(
        string text = "Hello world",
        string sourceLang = "en",
        string targetLang = "zh-Hans",
        string systemPrompt = "Translate faithfully.",
        string userPrompt = "Hello world")
    {
        return new ProviderTranslateRequest(text, sourceLang, targetLang)
        {
            PreparedPrompt = new RenderedPrompt(systemPrompt, userPrompt)
        };
    }

    [Fact]
    public void Create_WithSameInputs_ReturnsIdenticalKey()
    {
        var reg = CreateRegistration();
        var req1 = CreateRequest();
        var req2 = CreateRequest();

        var key1 = TranslationCacheKeyFactory.Create(reg, req1);
        var key2 = TranslationCacheKeyFactory.Create(reg, req2);

        key1.ShouldBe(key2);
        key1.ProviderId.ShouldBe(reg.Profile.Id);
        key1.InstanceId.ShouldBe(reg.InstanceId);
        key1.RequestFingerprint.Length.ShouldBe(64);
    }

    [Fact]
    public void Create_WithDifferentInstanceId_ReturnsDifferentKey()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "openai-1",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "OpenAI",
            Model = "gpt-4o",
            BaseUrl = "https://api.example.com",
            TimeoutSeconds = 30,
        };
        var provider = Substitute.For<ITranslationProvider>();
        var reg1 = new TranslationProviderRegistration(profile, provider, Guid.NewGuid());
        var reg2 = new TranslationProviderRegistration(profile, provider, Guid.NewGuid());
        var req = CreateRequest();

        var key1 = TranslationCacheKeyFactory.Create(reg1, req);
        var key2 = TranslationCacheKeyFactory.Create(reg2, req);

        key1.InstanceId.ShouldNotBe(key2.InstanceId);
        key1.RequestFingerprint.ShouldBe(key2.RequestFingerprint);
        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void Create_WithDifferentProviderId_ReturnsDifferentKey()
    {
        var instanceId = Guid.NewGuid();
        var reg1 = CreateRegistration("provider-a", instanceId);
        var reg2 = CreateRegistration("provider-b", instanceId);
        var req = CreateRequest();

        var key1 = TranslationCacheKeyFactory.Create(reg1, req);
        var key2 = TranslationCacheKeyFactory.Create(reg2, req);

        key1.ProviderId.ShouldBe("provider-a");
        key2.ProviderId.ShouldBe("provider-b");
        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void Create_EachFieldChange_ChangesRequestFingerprint()
    {
        var reg = CreateRegistration();
        var baseReq = CreateRequest("Text", "en", "zh-Hans", "Sys", "User");
        var baseKey = TranslationCacheKeyFactory.Create(reg, baseReq);

        var reqDifferentText = CreateRequest("TextModified", "en", "zh-Hans", "Sys", "User");
        TranslationCacheKeyFactory.Create(reg, reqDifferentText).RequestFingerprint.ShouldNotBe(baseKey.RequestFingerprint);

        var reqDifferentSource = CreateRequest("Text", "ja", "zh-Hans", "Sys", "User");
        TranslationCacheKeyFactory.Create(reg, reqDifferentSource).RequestFingerprint.ShouldNotBe(baseKey.RequestFingerprint);

        var reqDifferentTarget = CreateRequest("Text", "en", "es", "Sys", "User");
        TranslationCacheKeyFactory.Create(reg, reqDifferentTarget).RequestFingerprint.ShouldNotBe(baseKey.RequestFingerprint);

        var reqDifferentSys = CreateRequest("Text", "en", "zh-Hans", "SysModified", "User");
        TranslationCacheKeyFactory.Create(reg, reqDifferentSys).RequestFingerprint.ShouldNotBe(baseKey.RequestFingerprint);

        var reqDifferentUser = CreateRequest("Text", "en", "zh-Hans", "Sys", "UserModified");
        TranslationCacheKeyFactory.Create(reg, reqDifferentUser).RequestFingerprint.ShouldNotBe(baseKey.RequestFingerprint);
    }

    [Fact]
    public void Create_WithEmbeddedNul_DistinguishesFieldBoundaries()
    {
        var reg = CreateRegistration();

        // 验证长度前缀避免字段边界歧义，即使字段内包含 NUL 字符
        var req1 = CreateRequest("A\0B", "C", "D", "E", "F");
        var req2 = CreateRequest("A", "\0BC", "D", "E", "F");
        var req3 = CreateRequest("A", "B\0C", "D", "E", "F");

        var key1 = TranslationCacheKeyFactory.Create(reg, req1);
        var key2 = TranslationCacheKeyFactory.Create(reg, req2);
        var key3 = TranslationCacheKeyFactory.Create(reg, req3);

        key1.RequestFingerprint.ShouldNotBe(key2.RequestFingerprint);
        key2.RequestFingerprint.ShouldNotBe(key3.RequestFingerprint);
        key1.RequestFingerprint.ShouldNotBe(key3.RequestFingerprint);
    }

    [Fact]
    public void Create_WithNonAsciiCharacters_ProducesValidFingerprint()
    {
        var reg = CreateRegistration();
        var req = CreateRequest(
            text: "こんにちは世界 🌍 复杂的测试文本",
            sourceLang: "ja",
            targetLang: "zh-Hans",
            systemPrompt: "Вы профессиональный переводчик.",
            userPrompt: "こんにちは世界 🌍");

        var key = TranslationCacheKeyFactory.Create(reg, req);

        key.RequestFingerprint.Length.ShouldBe(64);
        key.RequestFingerprint.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Create_WithIsolatedSurrogate_DoesNotThrow_AndProducesValidFingerprint()
    {
        var reg = CreateRegistration();
        // 孤立的高代理项（D800），在普通 UTF-8 编码下可能失败或被替换为 U+FFFD，但 UTF-16LE 码元保留
        var req = CreateRequest(
            text: "InvalidSurrogate\uD800End",
            sourceLang: "auto",
            targetLang: "en",
            systemPrompt: "sys",
            userPrompt: "user");

        var key = TranslationCacheKeyFactory.Create(reg, req);

        key.RequestFingerprint.Length.ShouldBe(64);
        key.RequestFingerprint.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Create_MatchesIndependentSha256Calculation_ForKnownVector()
    {
        var reg = CreateRegistration("fixed-provider", Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var req = CreateRequest("Hello", "en", "zh-Hans", "System prompt", "User prompt");

        // 独立手动计算预期的 SHA-256
        using var ms = new System.IO.MemoryStream();
        void WriteField(string s)
        {
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)s.Length);
            ms.Write(lenBytes, 0, 4);
            var charBytes = Encoding.Unicode.GetBytes(s);
            ms.Write(charBytes, 0, charBytes.Length);
        }

        WriteField("Hello");
        WriteField("en");
        WriteField("zh-Hans");
        WriteField("System prompt");
        WriteField("User prompt");

        var expectedHashBytes = SHA256.HashData(ms.ToArray());
        var expectedHex = Convert.ToHexStringLower(expectedHashBytes);

        var key = TranslationCacheKeyFactory.Create(reg, req);

        key.ProviderId.ShouldBe("fixed-provider");
        key.InstanceId.ShouldBe(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        key.RequestFingerprint.ShouldBe(expectedHex);
    }

    [Fact]
    public void Create_KeyObject_DoesNotContainTextPromptOrCredentials()
    {
        var reg = CreateRegistration("my-provider", Guid.NewGuid());
        var req = CreateRequest("Secret Original Text", "en", "zh-Hans", "Secret System Prompt", "Secret User Prompt");

        var key = TranslationCacheKeyFactory.Create(reg, req);

        var keyString = key.ToString();
        keyString.ShouldNotContain("Secret Original Text");
        keyString.ShouldNotContain("Secret System Prompt");
        keyString.ShouldNotContain("Secret User Prompt");
    }

    [Fact]
    public void Create_NullValidation_ThrowsExpectedExceptions()
    {
        var reg = CreateRegistration();
        var reqWithNullPreparedPrompt = new ProviderTranslateRequest("Text", "en", "zh-Hans");

        Should.Throw<ArgumentNullException>(() => TranslationCacheKeyFactory.Create(null!, reqWithNullPreparedPrompt));
        Should.Throw<ArgumentNullException>(() => TranslationCacheKeyFactory.Create(reg, null!));
        var ex = Should.Throw<ArgumentException>(() => TranslationCacheKeyFactory.Create(reg, reqWithNullPreparedPrompt));
        ex.Message.ShouldContain("PreparedPrompt");
    }
}
