namespace Iris.Core.Storage;

/// <summary>
/// 数据目录：默认 %APPDATA%\Iris；若 exe 同目录存在 portable.txt 则为 {exe目录}\data（便携模式）。
/// 实现需在构造时确保目录存在。
/// </summary>
public interface IAppPaths
{
    bool IsPortable { get; }
    string DataDirectory { get; }
    string SettingsFile { get; }
    string SecretsFile { get; }
    string DatabaseFile { get; }
    string LogDirectory { get; }
}
