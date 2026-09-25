using System;
using Iris.Core.Security;
using Iris.Platform.Windows.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iris.Platform.Windows.DependencyInjection;

public static class PlatformWindowsServiceCollectionExtensions
{
    /// <summary>注册 Platform.Windows 层服务。调用方须事先注册 IAppPaths 与日志。</summary>
    public static IServiceCollection AddIrisPlatformWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        return services;
    }
}
