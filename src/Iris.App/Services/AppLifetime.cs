using System;
using System.Windows;
using Iris.App.Hosting;
using Iris.Core.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.App.Services;

/// <summary>
/// 应用程序生命周期服务：协调全系统的优雅退出。
/// </summary>
internal sealed class AppLifetime : IAppLifetime
{
    private readonly IServiceProvider _serviceProvider;

    public AppLifetime(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void RequestShutdown()
    {
        var app = Application.Current;
        if (app != null)
        {
            app.Dispatcher.InvokeAsync(async () =>
            {
                var bootstrapper = _serviceProvider.GetRequiredService<AppBootstrapper>();
                await bootstrapper.StopAsync();
            });
        }
    }
}
