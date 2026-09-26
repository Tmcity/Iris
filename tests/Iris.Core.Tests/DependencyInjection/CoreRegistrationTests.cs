using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Iris.Core.DependencyInjection;
using Iris.Core.Languages;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.DependencyInjection;

public sealed class CoreRegistrationTests
{
    [Fact]
    public void AddIrisCore_WithSettingsService_ResolvesAllServicesAsSingletons()
    {
        var services = new ServiceCollection();
        var settingsMock = Substitute.For<ISettingsService>();
        services.AddSingleton(settingsMock);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddIrisCore();

        using var provider = services.BuildServiceProvider();

        var catalog1 = provider.GetService<ILanguageCatalog>();
        var catalog2 = provider.GetService<ILanguageCatalog>();
        catalog1.ShouldNotBeNull();
        catalog1.ShouldBeSameAs(catalog2);

        var detector1 = provider.GetService<ILanguageDetector>();
        var detector2 = provider.GetService<ILanguageDetector>();
        detector1.ShouldNotBeNull();
        detector1.ShouldBeSameAs(detector2);

        var resolver1 = provider.GetService<ITargetLanguageResolver>();
        var resolver2 = provider.GetService<ITargetLanguageResolver>();
        resolver1.ShouldNotBeNull();
        resolver1.ShouldBeSameAs(resolver2);

        var preprocessor1 = provider.GetService<ITextPreprocessor>();
        var preprocessor2 = provider.GetService<ITextPreprocessor>();
        preprocessor1.ShouldNotBeNull();
        preprocessor1.ShouldBeSameAs(preprocessor2);

        var renderer1 = provider.GetService<IPromptRenderer>();
        var renderer2 = provider.GetService<IPromptRenderer>();
        renderer1.ShouldNotBeNull();
        renderer1.ShouldBeSameAs(renderer2);

        var templateProvider1 = provider.GetService<IPromptTemplateProvider>();
        var templateProvider2 = provider.GetService<IPromptTemplateProvider>();
        templateProvider1.ShouldNotBeNull();
        templateProvider1.ShouldBeSameAs(templateProvider2);
    }

    [Fact]
    public void AddIrisCore_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        var settingsMock = Substitute.For<ISettingsService>();
        services.AddSingleton(settingsMock);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddIrisCore();
        services.AddIrisCore();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<ILanguageCatalog>().Count().ShouldBe(1);
        provider.GetServices<ILanguageDetector>().Count().ShouldBe(1);
        provider.GetServices<ITargetLanguageResolver>().Count().ShouldBe(1);
        provider.GetServices<ITextPreprocessor>().Count().ShouldBe(1);
        provider.GetServices<IPromptRenderer>().Count().ShouldBe(1);
        provider.GetServices<IPromptTemplateProvider>().Count().ShouldBe(1);
    }
}
