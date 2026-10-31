using System.Diagnostics;
using Iris.Platform.Windows.Maintenance;

namespace Iris.Maintenance;

internal sealed class MaintenanceEngine(MaintenanceSystem system)
{
    private static MaintenanceTransaction Transaction(string kind, string phase, string version = "", bool delete = false)
    {
        using var process = Process.GetCurrentProcess();
        return new(Guid.NewGuid(), kind, phase, process.Id, process.StartTime.ToUniversalTime().Ticks, version, delete);
    }
    internal async Task InstallAsync(string setup, bool start, CancellationToken token, string? expectedVersion = null, bool repair = false)
    {
        using (var gate = MaintenanceGate.Acquire(system.StateRoot, recovery: repair))
        {
            bool pendingUninstall = false;
            var previous = gate.Read();
            if (previous != null)
            {
                RequireAvailableOwner(previous);
                if (previous.Phase is not ("preparing" or "awaiting-commit")) system.EnsureNoNativeWorker(previous);
                pendingUninstall = previous.Kind is "uninstall" or "install" && previous.DeleteData;
            }
            var state = Transaction("install", "preparing", expectedVersion ?? "", pendingUninstall); gate.Write(state);
            try
            {
                await system.CloseApplicationsAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await system.PrepareRuntimeAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch { if (previous == null) gate.Complete(); else gate.Write(previous); throw; }
            gate.Write(state = state with { Phase = "native-starting", WorkerPath = setup });
            int code;
            try { code = await system.RunAsync(setup, ["--silent"], (pid, ticks) => gate.Write(state = state with { Phase = "native-running", WorkerPid = pid, WorkerStartedUtcTicks = ticks })).ConfigureAwait(false); }
            catch (Exception primary)
            {
                try { if (system.LastNativeExitCode is int exit) gate.Write(state with { Phase = "native-record-failed", WorkerExitCode = exit }); }
                catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                throw;
            }
            gate.Write(state = state with { Phase = "verify", WorkerExitCode = code });
            if (code != 0) throw new IOException("Installation did not complete successfully.");
            var identity = ReleaseIdentity.Read(Path.Combine(system.InstallRoot, "current", ReleaseIdentity.FileName));
            if (expectedVersion != null && identity.Version != expectedVersion) throw new IOException("Installed version does not match the installer.");
            gate.Write(state with { Phase = "registration", Version = identity.Version });
            await system.ConfigureAsync().ConfigureAwait(false);
            if (pendingUninstall)
            {
                gate.Write(Transaction("uninstall", "preparing", delete: true));
                throw new IOException("Installation was repaired; resume the retained uninstall choice from the recovery entry.");
            }
            gate.Complete();
        }
        if (start) system.StartApplication();
    }
    internal async Task UninstallAsync(bool delete, CancellationToken token, bool allowRetainedDelete = true)
    {
        using var gate = MaintenanceGate.Acquire(system.StateRoot, recovery: true);
        var prior = gate.Read();
        if (prior != null)
        {
            RequireAvailableOwner(prior);
            if (prior.Kind == "uninstall")
            {
                if (prior.DeleteData && !allowRetainedDelete) throw new IOException("The retained data deletion requires interactive recovery.");
                await ResumeUninstallAsync(gate, prior, token).ConfigureAwait(false); return;
            }
            // A failed install/update must be repaired before starting a different native transaction.
            throw new IOException("Complete installation recovery before uninstalling.");
        }
        _ = ReleaseIdentity.Read(Path.Combine(system.InstallRoot, "current", ReleaseIdentity.FileName));
        var state = Transaction("uninstall", "preparing", delete: delete); gate.Write(state);
        await UninstallCoreAsync(gate, state, token).ConfigureAwait(false);
    }
    private async Task UninstallCoreAsync(MaintenanceGate gate, MaintenanceTransaction state, CancellationToken token, MaintenanceTransaction? previous = null)
    {
        try { await system.CloseApplicationsAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
        catch { if (previous == null) gate.Complete(); else gate.Write(previous); throw; }
        string worker = Path.Combine(system.InstallRoot, "Update.exe");
        gate.Write(state = state with { Phase = "native-starting", WorkerPath = worker });
        int code;
        try { code = await system.RunAsync(worker, ["--uninstall", "--silent"],
            (pid, ticks) => gate.Write(state = state with { Phase = "native-running", WorkerPid = pid, WorkerStartedUtcTicks = ticks })).ConfigureAwait(false); }
        catch (Exception primary)
        {
            try { if (system.LastNativeExitCode is int exit) gate.Write(state with { Phase = "native-record-failed", WorkerExitCode = exit }); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
        gate.Write(state = state with { Phase = "verify", WorkerExitCode = code });
        if (code != 0 || !system.IsUninstalled) throw new IOException("Uninstall did not complete.");
        gate.Write(state = state with { Phase = "data-cleanup" });
        system.RemoveAutoStart(); if (state.DeleteData) system.DeleteData(); gate.Complete();
    }
    private async Task ResumeUninstallAsync(MaintenanceGate gate, MaintenanceTransaction state, CancellationToken token)
    {
        if (state.WorkerExitCode == 0 && system.IsUninstalled)
        {
            await system.CloseApplicationsAsync(token).ConfigureAwait(false);
            system.RemoveAutoStart(); if (state.DeleteData) system.DeleteData(); gate.Complete(); return;
        }
        if (state.Phase is not ("preparing" or "awaiting-commit")) system.EnsureNoNativeWorker(state);
        _ = ReleaseIdentity.Read(Path.Combine(system.InstallRoot, "current", ReleaseIdentity.FileName));
        // This resumes only an incomplete native uninstall, retaining the already confirmed data choice.
        var retry = Transaction("uninstall", "preparing", delete: state.DeleteData); gate.Write(retry);
        await UninstallCoreAsync(gate, retry, token, state).ConfigureAwait(false);
    }
    internal async Task RecoverAsync(CancellationToken token, bool allowRetainedDelete = true)
    {
        using var gate = MaintenanceGate.Acquire(system.StateRoot, recovery: true);
        var state = gate.Read(); if (state == null) return;
        RequireAvailableOwner(state);
        if (state.DeleteData && !allowRetainedDelete) throw new IOException("The retained data deletion requires interactive recovery.");
        if (state.Kind == "uninstall") { await ResumeUninstallAsync(gate, state, token).ConfigureAwait(false); return; }
        if (state.Phase == "preparing" || state.Phase == "awaiting-commit") { gate.Complete(); return; }
        if (state.Phase is "native-starting" or "native-running") throw new IOException("Native result is unknown; explicit installation repair is required.");
        if (state.WorkerExitCode != 0) throw new IOException("Native success was not recorded; explicit installation repair is required.");
        await system.CloseApplicationsAsync(token).ConfigureAwait(false);
        if (state.Kind is "install" or "update" && state.Phase is "verify" or "registration" && !string.IsNullOrEmpty(state.Version))
        {
            var identity = ReleaseIdentity.Read(Path.Combine(system.InstallRoot, "current", ReleaseIdentity.FileName));
            if (!string.IsNullOrEmpty(state.Version) && identity.Version != state.Version) throw new IOException("Installation state needs repair.");
            await system.ConfigureAsync().ConfigureAwait(false);
            if (state.Kind == "install" && state.DeleteData)
            {
                var pending = Transaction("uninstall", "preparing", delete: true); gate.Write(pending);
                await ResumeUninstallAsync(gate, pending, token).ConfigureAwait(false); return;
            }
            gate.Complete(); return;
        }
        throw new IOException("Maintenance state needs repair.");
    }
    private static void RequireAvailableOwner(MaintenanceTransaction state)
    {
        using var current = Process.GetCurrentProcess();
        bool ownedHere = state.OwnerPid == current.Id && state.OwnerStartedUtcTicks == current.StartTime.ToUniversalTime().Ticks;
        if ((!ownedHere && MaintenanceSystem.IsAlive(state.OwnerPid, state.OwnerStartedUtcTicks))
            || (state.WorkerPid is int worker && state.WorkerStartedUtcTicks is long ticks && MaintenanceSystem.IsAlive(worker, ticks)))
            throw new IOException("Maintenance is still running.");
    }
}
