using System;
using Iris.Core.Languages;
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

        return services;
    }
}
