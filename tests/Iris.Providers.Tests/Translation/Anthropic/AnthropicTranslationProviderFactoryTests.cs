using System;
using System.Net.Http;
using Iris.Core.Common;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Translation.Anthropic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Translation.Anthropic;

public sealed class AnthropicTranslationProviderFactoryTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IPromptTemplateProvider _promptTemplates = Substitute.For<IPromptTemplateProvider>();
    private readonly IPromptRenderer _promptRenderer = Substitute.For<IPromptRenderer>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    private AnthropicTranslationProviderFactory CreateFactory() =>
        new(_httpClientFactory, _promptTemplates, _promptRenderer, _settings, _timeProvider, _loggerFactory);

    [Fact]
    public void Kind_IsAnthropic()
    {
        var factory = CreateFactory();
        factory.Kind.ShouldBe(TranslationProviderKind.Anthropic);
    }

    [Fact]
    public void Create_ReturnsProviderImplementingBothInterfaces()
    {
        var factory = CreateFactory();
        var profile = new TranslationProviderProfile
        {
            Id = "test-anthropic",
            DisplayName = "Test Anthropic Provider",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-opus-5",
        };
        var credentials = new ProviderCredentials("sk-ant-12345678");

        var provider = factory.Create(profile, credentials);

        provider.ShouldNotBeNull();
        provider.ShouldBeAssignableTo<ITranslationProvider>();
        provider.ShouldBeAssignableTo<IModelCatalogSource>();
    }

    [Fact]
    public void Create_KindMismatch_ThrowsArgumentException()
    {
        var factory = CreateFactory();
        var profile = new TranslationProviderProfile
        {
            Id = "test-provider",
            DisplayName = "Test Provider",
            Kind = TranslationProviderKind.OpenAICompatible,
            Model = "gpt-4o",
        };
        var credentials = new ProviderCredentials("sk-12345678");

        Should.Throw<ArgumentException>(() => factory.Create(profile, credentials));
    }

    [Fact]
    public void Create_ProfileWithNullAnthropicOptions_SucceedsWithDefaultOptions()
    {
        var factory = CreateFactory();
        var profile = new TranslationProviderProfile
        {
            Id = "test-anthropic",
            DisplayName = "Test Anthropic Provider",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-opus-5",
            AnthropicOptions = null,
        };
        var credentials = new ProviderCredentials("sk-ant-12345678");

        var provider = factory.Create(profile, credentials);
        provider.ShouldNotBeNull();
        provider.Descriptor.Id.ShouldBe("test-anthropic");
    }

    [Fact]
    public void Constructor_NullArguments_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new AnthropicTranslationProviderFactory(
            null!, _promptTemplates, _promptRenderer, _settings, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new AnthropicTranslationProviderFactory(
            _httpClientFactory, null!, _promptRenderer, _settings, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new AnthropicTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, null!, _settings, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new AnthropicTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, _promptRenderer, null!, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new AnthropicTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, _promptRenderer, _settings, null!, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new AnthropicTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, _promptRenderer, _settings, _timeProvider, null!));
    }

    [Fact]
    public void Create_NullArguments_ThrowsArgumentNullException()
    {
        var factory = CreateFactory();
        var profile = new TranslationProviderProfile
        {
            Id = "test",
            DisplayName = "Test",
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus-5",
        };
        var credentials = new ProviderCredentials("sk-ant-123");

        Should.Throw<ArgumentNullException>(() => factory.Create(null!, credentials));
        Should.Throw<ArgumentNullException>(() => factory.Create(profile, null!));
    }
}
