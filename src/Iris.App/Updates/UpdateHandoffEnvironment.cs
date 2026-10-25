using System.Diagnostics;
using System.IO;
using Iris.Platform.Windows.Maintenance;

namespace Iris.App.Updates;

internal class UpdateHandoffEnvironment
{
    internal virtual void ValidateInstallation()
    { if (!MaintenancePaths.IsInstalledApp(AppContext.BaseDirectory)) throw ReleaseIdentity.Invalid(); }
    internal virtual Task ValidateCandidateAsync(UpdateCandidate candidate, CancellationToken token)
        => ReleasePackage.ValidateAsync(candidate.Path, candidate.Size, candidate.Sha256, candidate.Version, token);
    internal virtual string HelperPath => Path.Combine(AppContext.BaseDirectory, "maintenance", "Iris.Maintenance.exe");
    internal virtual string StateRoot => MaintenancePaths.StateRoot;
    internal virtual Process StartHelper(ProcessStartInfo start) => Process.Start(start) ?? throw new IOException("Maintenance did not start.");
    internal virtual void DeleteWorkDirectory(string directory) => MaintenancePaths.DeleteData(directory, directory);
}
