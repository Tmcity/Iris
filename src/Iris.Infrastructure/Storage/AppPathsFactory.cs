using System;
using Iris.Core.Storage;

namespace Iris.Infrastructure.Storage;

/// <summary>
/// 提供应用程序路径工厂入口。
/// </summary>
public static class AppPathsFactory
{
    /// <summary>生产环境入口。在 DI 容器建立之前调用（日志需要最先初始化）。</summary>
    public static IAppPaths CreateDefault()
    {
        var exeDir = AppContext.BaseDirectory;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new AppPaths(exeDir, appData);
    }
}
