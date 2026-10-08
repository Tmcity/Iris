using Iris.App.Hosting;
using Iris.Core.Actions;
using Iris.Core.Storage;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;

namespace Iris.App.Tests.TestSupport;

internal static class IsolatedAppHost
{
    internal static IHost Build(string[] args, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp,
        Action<IServiceCollection>? configureTestOverrides = null, IpcCommand? initialCommand = null)
        => AppHostBuilder.Build(args, paths, levelSwitch, startTimestamp, services =>
        {
            services.AddSingleton(new IpcIdentity("Iris.Tests.Pipe." + Guid.NewGuid().ToString("N"), "Local\\Iris.Tests.Mutex." + Guid.NewGuid().ToString("N")));
            configureTestOverrides?.Invoke(services);
        }, initialCommand);
}
