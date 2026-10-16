using System.Diagnostics;

namespace Iris.Platform.Windows.Maintenance;

internal static class MaintenanceWorkDirectory
{
    internal static void Clean(string stateRoot)
    {
        if (!Directory.Exists(stateRoot)) return;
        MaintenancePaths.RequireNoLinks(stateRoot);
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("Iris.Maintenance"))
        {
            using (process)
            {
                if (process.HasExited) continue;
                try { if (process.MainModule?.FileName is string path) activePaths.Add(Path.GetFullPath(path)); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        var directories = Directory.GetDirectories(stateRoot, "work-*");
        foreach (var directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (name.Length != 37 || !Guid.TryParseExact(name[5..], "N", out _)) continue;
            string marker = Path.Combine(directory, "owned.json");
            if (!File.Exists(marker)) continue;
            MaintenancePaths.RequireNoLinks(marker);
            if (new FileInfo(marker).Length > 2048) continue;
            try
            {
                var owner = ReleaseIdentity.Parse<Owner>(File.ReadAllBytes(marker));
                if (owner.Marker != "Iris.Maintenance.Work.1" || IsAlive(owner.Pid, owner.Started) || activePaths.Contains(Path.Combine(directory, "Iris.Maintenance.exe"))) continue;
                MaintenancePaths.DeleteData(directory, directory);
            }
            catch (Exception) { }
        }
    }

    internal static string Create(string stateRoot)
    {
        MaintenancePaths.RequireNoLinks(stateRoot); Directory.CreateDirectory(stateRoot);
        Clean(stateRoot);
        if (Directory.GetDirectories(stateRoot, "work-*").Length >= 32) throw new IOException("Too many unfinished maintenance sessions.");
        string result = MaintenancePaths.Child(stateRoot, "work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(result);
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(result, "owned.json"), System.Text.Json.JsonSerializer.Serialize(new Owner("Iris.Maintenance.Work.1", current.Id, current.StartTime.ToUniversalTime().Ticks)));
        return result;
    }
    private static bool IsAlive(int pid, long ticks)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
    }
    private sealed record Owner(string Marker, int Pid, long Started);
}
