using System;
using System.Net.Http;
using Iris.Core.Common;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Translation.OpenAICompatible;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Translation.OpenAICompatible;

public sealed class OpenAICompatibleTranslationProviderFactoryTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IPromptTemplateProvider _promptTemplates = Substitute.For<IPromptTemplateProvider>();
    private readonly IPromptRenderer _promptRenderer = Substitute.For<IPromptRenderer>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    private OpenAICompatibleTranslationProviderFactory CreateFactory() =>
        new(_httpClientFactory, _promptTemplates, _promptRenderer, _settings, _timeProvider, _loggerFactory);

    [Fact]
    public void Kind_IsOpenAICompatible()
    {
        var factory = CreateFactory();
        factory.Kind.ShouldBe(TranslationProviderKind.OpenAICompatible);
    }

    [Fact]
    public void Create_ReturnsProviderImplementingBothInterfaces()
    {
        var factory = CreateFactory();
        var profile = new TranslationProviderProfile
        {
            Id = "test-provider",
            DisplayName = "Test Provider",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
        };
        var credentials = new ProviderCredentials("sk-12345678");

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
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus",
        };
        var credentials = new ProviderCredentials("sk-12345678");

        Should.Throw<ArgumentException>(() => factory.Create(profile, credentials));
    }

    [Fact]
    public void Create_ProfileWithNullOpenAIOptions_SucceedsWithDefaultOptions()
    {
        var factory = CreateFactory();
        var profile = new TranslationProviderProfile
        {
            Id = "test-provider",
            DisplayName = "Test Provider",
            Kind = TranslationProviderKind.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o",
            OpenAIOptions = null,
        };
        var credentials = new ProviderCredentials("sk-12345678");

        var provider = factory.Create(profile, credentials);
        provider.ShouldNotBeNull();
        provider.Descriptor.Id.ShouldBe("test-provider");
    }

    [Fact]
    public void Constructor_NullArguments_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new OpenAICompatibleTranslationProviderFactory(
            null!, _promptTemplates, _promptRenderer, _settings, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new OpenAICompatibleTranslationProviderFactory(
            _httpClientFactory, null!, _promptRenderer, _settings, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new OpenAICompatibleTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, null!, _settings, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new OpenAICompatibleTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, _promptRenderer, null!, _timeProvider, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new OpenAICompatibleTranslationProviderFactory(
            _httpClientFactory, _promptTemplates, _promptRenderer, _settings, null!, _loggerFactory));
        Should.Throw<ArgumentNullException>(() => new OpenAICompatibleTranslationProviderFactory(
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
            Kind = TranslationProviderKind.OpenAICompatible,
            Model = "gpt-4o",
        };
        var credentials = new ProviderCredentials("sk-12345678");

        Should.Throw<ArgumentNullException>(() => factory.Create(null!, credentials));
        Should.Throw<ArgumentNullException>(() => factory.Create(profile, null!));
    }
}
