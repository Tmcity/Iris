using Microsoft.Win32;
using Iris.Core.Errors;

namespace Iris.Platform.Windows.Maintenance;

internal static class MaintenanceRegistration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Iris";
    internal static void Configure(string installRoot)
    {
        if (!MaintenancePaths.Same(installRoot, MaintenancePaths.InstallRoot)) throw ReleaseIdentity.Invalid();
        var helper = Path.Combine(installRoot, "current", "maintenance", "Iris.Maintenance.exe");
        MaintenancePaths.RequireNoLinks(helper);
        if (!File.Exists(helper)) throw ReleaseIdentity.Invalid();
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true)
            ?? throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure));
        if (key.GetValue("InstallLocation") is not string location || !MaintenancePaths.Same(location, installRoot)) throw ReleaseIdentity.Invalid();
        string command = $"\"{helper}\" --uninstall";
        key.SetValue("UninstallString", command, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", command + " --silent", RegistryValueKind.String);
        if (!Equals(key.GetValue("UninstallString"), command) || !Equals(key.GetValue("QuietUninstallString"), command + " --silent")) throw ReleaseIdentity.Invalid();
    }
    internal static void RemoveOwnedAutoStart(string installRoot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key?.GetValue("Iris") is string current && current.Equals($"\"{Path.Combine(installRoot, "current", "Iris.App.exe")}\"", StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("Iris", false);
    }
    internal static bool IsUninstalled(string root)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return !File.Exists(Path.Combine(root, "current", "Iris.App.exe")) && key == null;
    }
}
