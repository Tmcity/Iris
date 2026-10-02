using System;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.DependencyInjection;
using Iris.Core.Presentation;
using Iris.Core.Storage;
using Iris.Infrastructure.DependencyInjection;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Providers.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;

namespace Iris.App.Hosting;

/// <summary>
/// 组合根：构建 Generic Host 并配置唯一的 Serilog 日志以及全部服务注册。
/// </summary>
internal static class AppHostBuilder
{
    public static IHost Build(string[] args, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(levelSwitch);

        var settings = new HostApplicationBuilderSettings
        {
            Args = args,
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        };

        var builder = Host.CreateApplicationBuilder(settings);

        // 启用依赖注入容器的构建期校验与作用域校验
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        // 日志接入 Serilog，禁止外部 Dispose 静态 Logger
        builder.Services.AddSerilog(Log.Logger, dispose: false);

        // 基础实例注册
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSingleton(new AppStartupInfo(startTimestamp));

        // 基础设施与平台层服务
        builder.Services.AddIrisInfrastructure();
        builder.Services.AddIrisPlatformWindows();

        // Core 层服务
        builder.Services.AddIrisCore();
        builder.Services.AddIrisProviders();

        // App 层单例服务
        builder.Services.AddSingleton<NotificationThrottle>();
        builder.Services.AddSingleton<GlobalExceptionHandler>();
        builder.Services.AddSingleton<LogLevelController>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<TrayIconController>();
        builder.Services.AddSingleton<TrayNotificationService>();
        builder.Services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<TrayNotificationService>());
        builder.Services.AddSingleton<IAppLifetime, AppLifetime>();
        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        builder.Services.AddSingleton<IActionDispatcher, ActionDispatcher>();

        // 设置窗口及编辑协调服务
        builder.Services.AddSingleton<Iris.App.Services.ISettingsApplyReceiptManager, Iris.App.Services.SettingsApplyReceiptManager>();
        builder.Services.AddSingleton<Iris.App.Settings.SettingsWindowPresenter>();
        builder.Services.AddSingleton<ISettingsWindowPresenter>(sp => sp.GetRequiredService<Iris.App.Settings.SettingsWindowPresenter>());
        builder.Services.AddSingleton<Iris.App.Settings.ISettingsShutdownCoordinator>(sp => sp.GetRequiredService<Iris.App.Settings.SettingsWindowPresenter>());
        builder.Services.AddScoped<Iris.App.Services.GeneralSettingsSaveCoordinator>();
        builder.Services.AddScoped<Iris.App.Settings.SettingsViewModel>();
        builder.Services.AddTransient<Iris.App.Settings.SettingsWindow>();

        // 翻译窗口及展示器服务
        builder.Services.AddSingleton<Iris.App.Translation.TranslateWindowPresenter>();
        builder.Services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<Iris.App.Translation.TranslateWindowPresenter>());
        builder.Services.AddSingleton<Iris.App.Translation.ITranslateWindowShutdownCoordinator>(sp => sp.GetRequiredService<Iris.App.Translation.TranslateWindowPresenter>());
        builder.Services.AddScoped<Iris.App.Translation.TranslateWindowViewModel>();
        builder.Services.AddTransient<Iris.App.Translation.TranslateWindow>();

        builder.Services.AddSingleton<AppBootstrapper>();
        builder.Services.AddSingleton<App>();

        // 主机生命周期：使用 WpfHostLifetime 替换默认的 ConsoleLifetime
        builder.Services.AddSingleton<IHostLifetime, WpfHostLifetime>();

        return builder.Build();
    }
}
