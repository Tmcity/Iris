using System;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Infrastructure.Settings;
using Iris.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iris.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册 Infrastructure 层服务。调用方须事先注册 IAppPaths 实例（由 AppPathsFactory.CreateDefault() 创建）。</summary>
    public static IServiceCollection AddIrisInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<ITranslationHistoryRepository, SqliteHistoryRepository>();

        return services;
    }
}
