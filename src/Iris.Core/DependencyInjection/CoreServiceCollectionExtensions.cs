using System;
using Iris.Core.Languages;
using Iris.Core.Translation;
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

        return services;
    }
}
