using System.Security.Cryptography;
using Iris.Platform.Windows.Maintenance;

namespace Iris.Maintenance;

internal static class MaintenanceCopy
{
    internal static string RecoveryPath => Path.Combine(MaintenancePaths.StateRoot, "recovery", "Iris.Maintenance.exe");
    internal static async Task PreserveRecoveryAsync(string source, CancellationToken token)
    {
        string target = RecoveryPath; MaintenancePaths.RequireNoLinks(source); MaintenancePaths.RequireNoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!MaintenancePaths.Same(source, target))
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await using var file = File.OpenRead(target); var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        MaintenancePaths.RequireNoLinks(target + ".sha256");
        await File.WriteAllTextAsync(target + ".sha256", Convert.ToHexString(hash), token).ConfigureAwait(false);
    }
    internal static async Task VerifyAsync(string executable, CancellationToken token)
    {
        string installed = Path.Combine(MaintenancePaths.InstallRoot, "current", "maintenance", "Iris.Maintenance.exe");
        if (!Path.GetFullPath(executable).StartsWith(Path.TrimEndingDirectorySeparator(MaintenancePaths.StateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw ReleaseIdentity.Invalid();
        MaintenancePaths.RequireNoLinks(executable); MaintenancePaths.RequireNoLinks(installed);
        await using var file = File.OpenRead(executable); byte[] hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        if (File.Exists(installed))
        {
            await using var source = File.OpenRead(installed); byte[] expected = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
            if (CryptographicOperations.FixedTimeEquals(hash, expected)) return;
            // A retained recovery executable can predate a successfully replaced installation.
            // It must still match the recorded recovery copy and an unresolved product transaction.
            if (!MaintenanceGate.IsBlocked(MaintenancePaths.StateRoot)) throw ReleaseIdentity.Invalid();
        }
        string record = RecoveryPath + ".sha256"; MaintenancePaths.RequireNoLinks(record);
        if (!MaintenancePaths.Same(executable, RecoveryPath) || !File.Exists(record) || new FileInfo(record).Length != 64) throw ReleaseIdentity.Invalid();
        if (!Convert.ToHexString(hash).Equals(await File.ReadAllTextAsync(record, token).ConfigureAwait(false), StringComparison.Ordinal)) throw ReleaseIdentity.Invalid();
    }
}
