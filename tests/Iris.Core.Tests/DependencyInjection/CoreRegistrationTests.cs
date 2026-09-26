using Iris.Core.DependencyInjection;
using Iris.Core.Languages;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.DependencyInjection;

public sealed class CoreRegistrationTests
{
    [Fact]
    public void AddIrisCore_WithSettingsService_ResolvesAllThreeServicesAsSingletons()
    {
        var services = new ServiceCollection();
        var settingsMock = Substitute.For<ISettingsService>();
        services.AddSingleton(settingsMock);

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
    }

    [Fact]
    public void AddIrisCore_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        var settingsMock = Substitute.For<ISettingsService>();
        services.AddSingleton(settingsMock);

        services.AddIrisCore();
        services.AddIrisCore();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<ILanguageCatalog>().Count().ShouldBe(1);
        provider.GetServices<ILanguageDetector>().Count().ShouldBe(1);
        provider.GetServices<ITargetLanguageResolver>().Count().ShouldBe(1);
    }
}
