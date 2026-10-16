using System.Text.Json;
using Iris.Maintenance;
using Iris.Platform.Windows.Maintenance;

internal static class RecoveryProbe
{
    internal static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "iris-maintenance-recovery-review-" + Guid.NewGuid().ToString("N"));
        string current = Path.Combine(root, "current");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(current);
        string identity = Path.Combine(current, ReleaseIdentity.FileName);
        await File.WriteAllTextAsync(identity, JsonSerializer.Serialize(new ReleaseIdentity("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, "0.1.0"), ReleaseIdentity.Json));
        var system = new ControlledSystem(root, state);
        var engine = new MaintenanceEngine(system);
        string? firstError = null, retryError = null;
        bool blockedBeforeRetry = false, blockedAfterRetry = false;
        try
        {
            try { await engine.UninstallAsync(true, CancellationToken.None); }
            catch (Exception error) { firstError = error.GetType().Name; }
            blockedBeforeRetry = MaintenanceGate.IsBlocked(state);
            try { await engine.RecoverAsync(CancellationToken.None); }
            catch (Exception error) { retryError = error.GetType().Name; }
            blockedAfterRetry = MaintenanceGate.IsBlocked(state);
            bool passed = firstError == "IOException" && retryError == null && system.DeleteAttempts == 2
                && system.NativeRuns == 1 && blockedBeforeRetry && !blockedAfterRetry;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                FirstFailure = firstError,
                RetryFailure = retryError,
                NativeCalls = system.NativeRuns,
                DeleteAttempts = system.DeleteAttempts,
                BlockedBeforeRetry = blockedBeforeRetry,
                BlockedAfterRetry = blockedAfterRetry,
                ActualNativeInstallerRun = false,
                ActualUserDataTouched = false,
                Passed = passed
            }));
            return passed ? 0 : 1;
        }
        finally
        {
            // Teardown removes only the explicitly created fake state; it is not a product recovery result.
            using (var gate = MaintenanceGate.Acquire(state, recovery: true)) gate.Complete();
            File.Delete(identity);
            File.Delete(Path.Combine(state, "transaction.lock"));
            Directory.Delete(current);
            Directory.Delete(state);
            Directory.Delete(root);
        }
    }

    private sealed class ControlledSystem(string root, string state) : MaintenanceSystem
    {
        internal int NativeRuns, DeleteAttempts;
        private bool _uninstalled;
        internal override string InstallRoot => root;
        internal override string StateRoot => state;
        internal override bool IsUninstalled => _uninstalled;
        internal override Task CloseApplicationsAsync(CancellationToken token) => Task.CompletedTask;
        internal override Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, long> started)
        {
            // External native mutation is replaced by its completed success; no process is launched.
            NativeRuns++; _uninstalled = true;
            return Task.FromResult(0);
        }
        internal override void RemoveAutoStart() { }
        internal override void DeleteData()
        {
            DeleteAttempts++;
            if (DeleteAttempts == 1) throw new IOException("synthetic first data cleanup failure");
        }
    }
}
