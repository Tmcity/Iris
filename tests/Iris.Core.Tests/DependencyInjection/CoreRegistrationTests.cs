using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Iris.Core.DependencyInjection;
using Iris.Core.Languages;
using Iris.Core.Settings;
using Iris.Core.Storage;
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
        settingsMock.Current.Returns(new AppSettings());
        var secretStoreMock = Substitute.For<Iris.Core.Security.ISecretStore>();
        var historyMock = Substitute.For<ITranslationHistoryRepository>();
        var presenterMock = Substitute.For<Iris.Core.Presentation.ISettingsWindowPresenter>();
        var translatePresenterMock = Substitute.For<Iris.Core.Presentation.ITranslateWindowPresenter>();
        var clipboardMock = Substitute.For<Iris.Core.Platform.IClipboardService>();
        services.AddSingleton(settingsMock);
        services.AddSingleton(secretStoreMock);
        services.AddSingleton(historyMock);
        services.AddSingleton(presenterMock);
        services.AddSingleton(translatePresenterMock);
        services.AddSingleton(clipboardMock);
        services.AddSingleton(Substitute.For<Iris.Core.Platform.ISelectionTextReader>());
        services.AddSingleton(Substitute.For<Iris.Core.Platform.IDesktopContext>());
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

        var registry1 = provider.GetService<ITranslationProviderRegistry>();
        var registry2 = provider.GetService<ITranslationProviderRegistry>();
        registry1.ShouldNotBeNull();
        registry1.ShouldBeSameAs(registry2);

        var observer1 = provider.GetService<IProviderConfigurationObserver>();
        var observer2 = provider.GetService<IProviderConfigurationObserver>();
        observer1.ShouldNotBeNull();
        observer1.ShouldBeSameAs(observer2);

        var cache1 = provider.GetService<ITranslationCache>();
        var cache2 = provider.GetService<ITranslationCache>();
        cache1.ShouldNotBeNull();
        cache1.ShouldBeSameAs(cache2);

        var orchestrator1 = provider.GetService<ITranslationOrchestrator>();
        var orchestrator2 = provider.GetService<ITranslationOrchestrator>();
        orchestrator1.ShouldNotBeNull();
        orchestrator1.ShouldBeSameAs(orchestrator2);

        var coordinator1 = provider.GetService<ISettingsEditCoordinator>();
        var coordinator2 = provider.GetService<ISettingsEditCoordinator>();
        coordinator1.ShouldNotBeNull();
        coordinator1.ShouldBeSameAs(coordinator2);

        var actionHandlers = provider.GetServices<Iris.Core.Actions.IActionHandler>().ToList();
        actionHandlers.Count.ShouldBe(6);
        actionHandlers.Single(h => h.Action == Iris.Core.Actions.IrisAction.SelectionTranslate).ShouldBeSameAs(provider.GetServices<Iris.Core.Actions.IActionHandler>().Single(h => h.Action == Iris.Core.Actions.IrisAction.SelectionTranslate));
        actionHandlers.FirstOrDefault(h => h.Action == Iris.Core.Actions.IrisAction.OpenSettings).ShouldNotBeNull();
        actionHandlers.FirstOrDefault(h => h.Action == Iris.Core.Actions.IrisAction.InputTranslate).ShouldNotBeNull();
        actionHandlers.FirstOrDefault(h => h.Action == Iris.Core.Actions.IrisAction.ClipboardTranslate).ShouldNotBeNull();
        actionHandlers.FirstOrDefault(h => h.Action == Iris.Core.Actions.IrisAction.TranslateText).ShouldNotBeNull();
        actionHandlers.Single(h => h.Action == Iris.Core.Actions.IrisAction.ToggleMouseSelection).ShouldBeSameAs(provider.GetServices<Iris.Core.Actions.IActionHandler>().Single(h => h.Action == Iris.Core.Actions.IrisAction.ToggleMouseSelection));
    }

    [Fact]
    public void AddIrisCore_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        var settingsMock = Substitute.For<ISettingsService>();
        settingsMock.Current.Returns(new AppSettings());
        var secretStoreMock = Substitute.For<Iris.Core.Security.ISecretStore>();
        var historyMock = Substitute.For<ITranslationHistoryRepository>();
        var presenterMock = Substitute.For<Iris.Core.Presentation.ISettingsWindowPresenter>();
        var translatePresenterMock = Substitute.For<Iris.Core.Presentation.ITranslateWindowPresenter>();
        var clipboardMock = Substitute.For<Iris.Core.Platform.IClipboardService>();
        services.AddSingleton(settingsMock);
        services.AddSingleton(secretStoreMock);
        services.AddSingleton(historyMock);
        services.AddSingleton(presenterMock);
        services.AddSingleton(translatePresenterMock);
        services.AddSingleton(clipboardMock);
        services.AddSingleton(Substitute.For<Iris.Core.Platform.ISelectionTextReader>());
        services.AddSingleton(Substitute.For<Iris.Core.Platform.IDesktopContext>());
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
        provider.GetServices<ITranslationProviderRegistry>().Count().ShouldBe(1);
        provider.GetServices<IProviderConfigurationObserver>().Count().ShouldBe(2);
        provider.GetServices<IProviderConfigurationObserver>().OfType<OcrProviderConfigurationObserver>().Count().ShouldBe(1);
        provider.GetServices<IProviderConfigurationObserver>().OfType<TranslationProviderConfigurationObserver>().Count().ShouldBe(1);
        provider.GetServices<ITranslationCache>().Count().ShouldBe(1);
        provider.GetServices<ITranslationOrchestrator>().Count().ShouldBe(1);
        provider.GetServices<ISettingsEditCoordinator>().Count().ShouldBe(1);
        provider.GetServices<Iris.Core.Actions.IActionHandler>().Count(h => h.Action == Iris.Core.Actions.IrisAction.OpenSettings).ShouldBe(1);
        provider.GetServices<Iris.Core.Actions.IActionHandler>().Count(h => h.Action == Iris.Core.Actions.IrisAction.InputTranslate).ShouldBe(1);
        provider.GetServices<Iris.Core.Actions.IActionHandler>().Count(h => h.Action == Iris.Core.Actions.IrisAction.ClipboardTranslate).ShouldBe(1);
        provider.GetServices<Iris.Core.Actions.IActionHandler>().Count(h => h.Action == Iris.Core.Actions.IrisAction.TranslateText).ShouldBe(1);
        provider.GetServices<Iris.Core.Actions.IActionHandler>().Count(h => h.Action == Iris.Core.Actions.IrisAction.SelectionTranslate).ShouldBe(1);
        provider.GetServices<Iris.Core.Actions.IActionHandler>().Count(h => h.Action == Iris.Core.Actions.IrisAction.ToggleMouseSelection).ShouldBe(1);
    }
}
