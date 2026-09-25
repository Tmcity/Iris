using System;
using System.IO;
using System.Linq;
using Iris.Core.Errors;
using Iris.Core.Storage;

namespace Iris.Infrastructure.Storage;

/// <summary>
/// 应用程序路径管理实现。支持标准漫游目录模式与便携模式。
/// </summary>
internal sealed class AppPaths : IAppPaths
{
    internal const string PortableMarkerFileName = "portable.txt";
    internal const string AppFolderName = "Iris";

    /// <param name="executableDirectory">exe 所在目录（生产环境为 AppContext.BaseDirectory）。</param>
    /// <param name="roamingAppDataDirectory">%APPDATA%（生产环境为 Environment.SpecialFolder.ApplicationData）。</param>
    internal AppPaths(string executableDirectory, string roamingAppDataDirectory)
    {
        var markerPath = Path.Combine(executableDirectory, PortableMarkerFileName);
        var isPortable = File.Exists(markerPath);

        IsPortable = isPortable;

        if (isPortable)
        {
            DataDirectory = Path.GetFullPath(Path.Combine(executableDirectory, "data"));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(roamingAppDataDirectory))
            {
                throw new IrisException(ErrorCode.StorageFailure, "无法创建数据目录", detail: "(ApplicationData 路径为空)");
            }

            DataDirectory = Path.GetFullPath(Path.Combine(roamingAppDataDirectory, AppFolderName));
        }

        SettingsFile = Path.GetFullPath(Path.Combine(DataDirectory, "settings.json"));
        SecretsFile = Path.GetFullPath(Path.Combine(DataDirectory, "secrets.dat"));
        DatabaseFile = Path.GetFullPath(Path.Combine(DataDirectory, "history.db"));
        LogDirectory = Path.GetFullPath(Path.Combine(DataDirectory, "logs"));

        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IrisException(ErrorCode.StorageFailure, "无法创建数据目录", innerException: ex, detail: DataDirectory);
        }
    }

    public bool IsPortable { get; }
    public string DataDirectory { get; }
    public string SettingsFile { get; }
    public string SecretsFile { get; }
    public string DatabaseFile { get; }
    public string LogDirectory { get; }
}
