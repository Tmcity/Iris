using System.Diagnostics;
using System.Text.Json;
using Iris.Maintenance;
using Iris.Platform.Windows.Maintenance;

// Owned test process only. Product protocol, candidate validation, transaction
// gate and parent-exit barrier are real; installation/registry/restart are inert.
if (args.Length != 9 || args[0] != "--handoff") return 2;
string root = Path.GetFullPath(args[5]);
if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
    || !Path.GetFileName(root).StartsWith("IrisTest_", StringComparison.Ordinal)) return 3;
string resultPath = MaintenancePaths.Child(root, args[6]);
using var cancel = EventWaitHandle.OpenExisting(args[7]);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var system = new ControlledSystem(root, args[8]);
var watch = Task.Run(() =>
{
    if (WaitHandle.WaitAny([cancel, timeout.Token.WaitHandle]) == 0) timeout.Cancel();
});
Exception? failure = null;
bool canceled = false;
try
{
    await new UpdateTransaction(system).RunAsync(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
        long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), timeout.Token);
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested) { canceled = true; }
catch (Exception error) { failure = error; }
finally
{
    await timeout.CancelAsync();
    await watch;
    File.WriteAllText(resultPath, JsonSerializer.Serialize(new
    {
        Failure = failure?.GetType().Name,
        Canceled = canceled,
        system.NativeCalls,
        system.Restarts,
        GateBlocked = MaintenanceGate.IsBlocked(system.StateRoot),
    }));
}
return failure == null ? 0 : 1;

sealed class ControlledSystem(string root, string parentImage) : MaintenanceSystem
{
    internal int NativeCalls, Restarts;
    internal override string InstallRoot => Path.Combine(root, "install");
    internal override string StateRoot => Path.Combine(root, "state");
    internal override string DataRoot => Path.Combine(root, "data");
    internal override void ValidateParent(Process parent, long ticks)
    {
        using var self = Process.GetCurrentProcess();
        if (parent.StartTime.ToUniversalTime().Ticks != ticks || parent.SessionId != self.SessionId
            || !MaintenancePaths.Same(parent.MainModule!.FileName, parentImage)) throw new InvalidOperationException("Owned parent identity mismatch.");
    }
    internal override Task CloseApplicationsAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    internal override Task ConfigureAsync() => Task.CompletedTask;
    internal override Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, long> started)
    {
        NativeCalls++;
        throw new InvalidOperationException("The live test parent must prevent native application.");
    }
    internal override void StartApplication() { Restarts++; }
}
