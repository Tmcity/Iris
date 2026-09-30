using System;
using System.IO;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Microsoft.Win32;

namespace Iris.Platform.Windows.SystemIntegration;

internal interface IAutoStartRegistry
{
    (string? Value, RegistryValueKind? Kind) GetRunValue(string valueName);
    void SetRunValue(string valueName, string value);
    void DeleteRunValue(string valueName);
}

internal sealed class WindowsAutoStartRegistry : IAutoStartRegistry
{
    private readonly string _subKeyPath;

    public WindowsAutoStartRegistry(string subKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _subKeyPath = subKeyPath ?? throw new ArgumentNullException(nameof(subKeyPath));
    }

    public (string? Value, RegistryValueKind? Kind) GetRunValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: false);
        if (key == null)
        {
            return (null, null);
        }

        var rawValue = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (rawValue == null)
        {
            return (null, null);
        }

        var kind = key.GetValueKind(valueName);
        return (rawValue as string, kind);
    }

    public void SetRunValue(string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
        if (key == null)
        {
            throw new InvalidOperationException("Failed to open or create registry subkey for writing");
        }

        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteRunValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: true);
        if (key == null)
        {
            return;
        }

        if (key.GetValue(valueName) != null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>
/// 管理 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 Iris 值（Platform.Windows 实现）。
/// 完整规则见 SETTINGS_EDITING.md §6。
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    public const string ValueName = "Iris";
    public const int MaxCommandLength = 260;

    private readonly IAutoStartRegistry _registry;
    private readonly Func<string?> _appHostPathProvider;

    public AutoStartService()
        : this(new WindowsAutoStartRegistry(), () => Environment.ProcessPath)
    {
    }

    internal AutoStartService(IAutoStartRegistry registry, Func<string?> appHostPathProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _appHostPathProvider = appHostPathProvider ?? throw new ArgumentNullException(nameof(appHostPathProvider));
    }

    public bool IsEnabled()
    {
        string? expectedCommand;
        try
        {
            expectedCommand = GetExpectedCommand();
        }
        catch (IrisException)
        {
            // 路径无效或非有效 apphost 时无法匹配任何合法注册
            return false;
        }

        try
        {
            var (value, kind) = _registry.GetRunValue(ValueName);
            if (value == null || kind != RegistryValueKind.String)
            {
                return false;
            }

            return string.Equals(value, expectedCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (IrisException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "开机启动配置读取失败",
                innerException: null,
                detail: $"IsEnabled:{ex.GetType().Name}");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (IsEnabled())
            {
                return;
            }

            var command = GetExpectedCommand();
            try
            {
                _registry.SetRunValue(ValueName, command);
            }
            catch (Exception ex)
            {
                throw new IrisException(
                    ErrorCode.StorageFailure,
                    "开机启动配置写入失败",
                    innerException: null,
                    detail: $"SetEnabled:Write:{ex.GetType().Name}");
            }
        }
        else
        {
            try
            {
                _registry.DeleteRunValue(ValueName);
            }
            catch (Exception ex)
            {
                throw new IrisException(
                    ErrorCode.StorageFailure,
                    "开机启动配置删除失败",
                    innerException: null,
                    detail: $"SetEnabled:Delete:{ex.GetType().Name}");
            }
        }
    }

    private string GetExpectedCommand()
    {
        string? path;
        try
        {
            path = _appHostPathProvider();
        }
        catch (Exception ex)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "获取应用程序路径失败",
                innerException: null,
                detail: $"AppHostPath:{ex.GetType().Name}");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "未找到有效的应用程序路径",
                innerException: null,
                detail: "AppHostPath:Empty");
        }

        var trimmedPath = path.Trim();
        if (!Path.IsPathRooted(trimmedPath) ||
            trimmedPath.Contains('\"') ||
            trimmedPath.Contains('\0') ||
            trimmedPath.Contains('\r') ||
            trimmedPath.Contains('\n'))
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "应用程序路径格式无效",
                innerException: null,
                detail: "AppHostPath:InvalidCharacters");
        }

        var fileName = Path.GetFileName(trimmedPath);
        if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "不能使用 dotnet 托管进程作为开机自启命令",
                innerException: null,
                detail: "AppHostPath:DotnetHostRejected");
        }

        if (!trimmedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "应用程序路径必须为可执行文件",
                innerException: null,
                detail: "AppHostPath:NotAnExecutable");
        }

        var command = $"\"{trimmedPath}\"";
        if (command.Length > MaxCommandLength)
        {
            throw new IrisException(
                ErrorCode.StorageFailure,
                "开机启动命令长度超出系统限制",
                innerException: null,
                detail: "AppHostPath:CommandLengthExceeded");
        }

        return command;
    }
}
