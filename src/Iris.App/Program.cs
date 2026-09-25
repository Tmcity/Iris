using System;
using System.Diagnostics;
using Iris.App.Hosting;
using Iris.Core.Errors;
using Iris.Core.Storage;
using Iris.Infrastructure.Storage;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Iris.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // 步骤 1：单实例锁检查（必须在创建日志器前执行）
        var guard = SingleInstanceGuard.TryAcquireForCurrentUser();
        if (guard == null)
        {
            return 0;
        }

        IHost? host = null;

        try
        {
            // 步骤 2：数据目录初始化
            IAppPaths paths;
            try
            {
                paths = AppPathsFactory.CreateDefault();
            }
            catch (Exception ex)
            {
                FatalErrorDialog.Show(UiText.AppName, FormatFatalMessage(ex));
                return 1;
            }

            // 步骤 3：初始化 Serilog 日志器
            var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
            Log.Logger = IrisLogging.CreateLogger(paths, levelSwitch);

            // 步骤 4：构建 Generic Host 并启动 App
            host = AppHostBuilder.Build(args, paths, levelSwitch, startTimestamp);
            var app = host.Services.GetRequiredService<App>();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            if (Log.Logger != null)
            {
                Log.ForContext(typeof(Program)).Fatal(ex, "程序启动阶段遇到致命异常");
            }

            FatalErrorDialog.Show(UiText.AppName, FormatFatalMessage(ex));
            return 1;
        }
        finally
        {
            host?.Dispose();
            Log.CloseAndFlush();
            guard.Dispose();
        }
    }

    private static string FormatFatalMessage(Exception ex)
    {
        if (ex is IrisException irisEx)
        {
            return string.IsNullOrEmpty(irisEx.Detail)
                ? irisEx.Message
                : $"{irisEx.Message}\n{irisEx.Detail}";
        }

        return $"{UiText.FatalUnknown}\n{ex.Message}";
    }
}
