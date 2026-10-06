using System;
using Iris.Core.Actions;
using Iris.Core.Languages;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iris.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>注册 Core 层服务。调用方须事先注册 ISettingsService（由 AddIrisInfrastructure 提供）。</summary>
    public static IServiceCollection AddIrisCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILanguageCatalog, LanguageCatalog>();
        services.TryAddSingleton<ILanguageDetector, ScriptLanguageDetector>();
        services.TryAddSingleton<ITargetLanguageResolver, TargetLanguageResolver>();

        services.TryAddSingleton<ITextPreprocessor, TextPreprocessor>();
        services.TryAddSingleton<IPromptRenderer, PromptRenderer>();
        services.TryAddSingleton<IPromptTemplateProvider, PromptTemplateProvider>();

        services.TryAddSingleton<TranslationProviderRegistry>();
        services.TryAddSingleton<ITranslationProviderRegistry>(sp => sp.GetRequiredService<TranslationProviderRegistry>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderConfigurationObserver, TranslationProviderConfigurationObserver>());
        services.TryAddSingleton<Iris.Core.Ocr.OcrProviderRegistry>();
        services.TryAddSingleton<Iris.Core.Ocr.IOcrProviderRegistry>(sp => sp.GetRequiredService<Iris.Core.Ocr.OcrProviderRegistry>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProviderConfigurationObserver, OcrProviderConfigurationObserver>());
        services.TryAddSingleton<Iris.Core.Ocr.OcrTextNormalizer>();
        services.TryAddSingleton<Iris.Core.Ocr.IOcrTextNormalizer>(sp => sp.GetRequiredService<Iris.Core.Ocr.OcrTextNormalizer>());
        services.TryAddSingleton<Iris.Core.Ocr.OcrService>();
        services.TryAddSingleton<Iris.Core.Ocr.IOcrService>(sp => sp.GetRequiredService<Iris.Core.Ocr.OcrService>());

        services.TryAddSingleton<ITranslationCache, LruTranslationCache>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITranslationOrchestrator, TranslationOrchestrator>();

        services.TryAddSingleton<IHotkeyBindingPolicy, HotkeyBindingPolicy>();
        services.TryAddSingleton<ISettingsEditCoordinator, SettingsEditCoordinator>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, OpenSettingsHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, InputTranslateHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, ClipboardTranslateHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, SelectionTranslateHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, ToggleMouseSelectionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, TranslateTextHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, ScreenshotTranslateHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, ScreenshotOcrHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActionHandler, SilentScreenshotOcrHandler>());

        return services;
    }
}
