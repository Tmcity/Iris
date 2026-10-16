using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Maintenance;
using Iris.Platform.Windows.Maintenance;

// This validation executable may run only in the owned guest. All filesystem/native operations
// use production MaintenanceSystem/UpdateTransaction. Only the control-parent executable path
// is substituted with this experiment's real PowerShell worker; the real Iris instance is closed
// gracefully before the worker commits/exits. This does not claim the production online UI path.
if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned guest only.");
if (args.Length > 0 && args[0] == "--data") return await GuestData.RunAsync(args.Skip(1).ToArray());
bool simulateNativeFailure = args.Length == 3 && args[2] == "--native-failure";
if (args.Length != 2 && !simulateNativeFailure) throw new InvalidOperationException("Owned guest arguments required.");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (!input.StartsWith(@"C:\IrisBInput\", StringComparison.OrdinalIgnoreCase) || !output.StartsWith(@"C:\IrisBOutput\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned input/output only.");
Directory.CreateDirectory(output);
var results = new List<object>();
void Record(string name, object value) { results.Add(new { Name = name, Value = value }); File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(results)); }
string pipe = "Iris.Maintenance." + Guid.NewGuid().ToString("N"), secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
using var pipeReady = new EventWaitHandle(false, EventResetMode.ManualReset, pipe + ".pipe");
using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, pipe + ".ready");
using var commit = new EventWaitHandle(false, EventResetMode.ManualReset, pipe + ".commit");
using var committed = new EventWaitHandle(false, EventResetMode.ManualReset, pipe + ".committed");
using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, pipe + ".exit");
using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Process? parent = null; Task? work = null; Exception? failure = null; var cleanup = new List<Exception>();
try
{
    var system = new GuestSystem { SimulateNativeFailure = simulateNativeFailure };
    string package = Path.Combine(system.InstallRoot, "packages", Path.GetFileName(input));
    File.Copy(input, package, true);
    string hash; long size;
    await using (var file = File.OpenRead(package)) { hash = Convert.ToHexString(await SHA256.HashDataAsync(file)); size = file.Length; }
    string version = Path.GetFileName(input)[5..^11];
    string offer = JsonSerializer.Serialize(new MaintenanceMessage("prepare", secret, Path.GetFileName(package), hash, size, version), ReleaseIdentity.Json);
    string script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parent.ps1")).Replace("PIPE_NAME", pipe, StringComparison.Ordinal)
        .Replace("OFFER_JSON", offer, StringComparison.Ordinal).Replace("TOKEN_VALUE", secret, StringComparison.Ordinal).Replace("SIGNAL_KIND", "commit", StringComparison.Ordinal);
    var start = new ProcessStartInfo(GuestSystem.PowerShell) { UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
    parent = Process.Start(start)!; _ = parent.Handle; system.ParentId = parent.Id;
    await Signal(pipeReady, guard.Token);
    work = new UpdateTransaction(system).RunAsync(pipe, secret, parent.Id, parent.StartTime.ToUniversalTime().Ticks, guard.Token);
    await Signal(ready, guard.Token);
    Record("ready", new { Gate = MaintenanceGate.IsBlocked(system.StateRoot), ParentAlive = !parent.HasExited });
    await system.CloseApplicationsAsync(guard.Token);
    Record("real-iris-gracefully-closed-before-commit", Process.GetProcessesByName("Iris.App").Length == 0);
    commit.Set(); await Signal(committed, guard.Token);
    if (parent.HasExited || work.IsCompleted) throw new InvalidOperationException("Parent barrier did not hold.");
    Record("committed-parent-still-alive", true); exit.Set(); await parent.WaitForExitAsync(guard.Token);
    if (parent.ExitCode != 0) throw new InvalidOperationException("Control parent failed.");
    await work;
    var identity = ReleaseIdentity.Read(Path.Combine(system.InstallRoot, "current", ReleaseIdentity.FileName));
    if (identity.Version != version || MaintenanceGate.IsBlocked(system.StateRoot)) throw new InvalidOperationException("Update final state failed.");
    Record("actual-update-completed", new { identity.Version, ParentExit = parent.ExitCode, system.LastNativeExitCode });
}
catch (Exception error)
{
    failure = error;
    if (simulateNativeFailure && error is CommittedUpdateException)
    {
        Record("committed-failure-before-ui", new { Gate = MaintenanceGate.IsBlocked(MaintenancePaths.StateRoot), Kind = error.GetType().Name });
        MaintenanceUi.Language = "en-US";
        await MaintenanceFeedback.ReportFailureAsync("guest-native-failure", false, true, error);
    }
}
finally
{
    commit.Set(); exit.Set(); await guard.CancelAsync();
    if (parent != null) { try { await parent.WaitForExitAsync(); } catch (Exception error) { cleanup.Add(error); } parent.Dispose(); }
    if (work != null) try { await work; } catch (Exception error) { if (!ReferenceEquals(error, failure)) cleanup.Add(error); }
    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Results = results, Failure = failure?.ToString(), Cleanup = cleanup.Select(error => error.ToString()).ToArray() }));
}
return failure == null && cleanup.Count == 0 ? 0 : 2;

static async Task Signal(EventWaitHandle signal, CancellationToken token)
{ int result = await Task.Run(() => WaitHandle.WaitAny([signal, token.WaitHandle], TimeSpan.FromSeconds(45)), token); if (result != 0) throw new TimeoutException("Owned control worker signal timeout."); }

sealed class GuestSystem : MaintenanceSystem
{
    internal int ParentId;
    internal bool SimulateNativeFailure;
    internal static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    internal override void ValidateParent(Process parent, long ticks)
    {
        using var self = Process.GetCurrentProcess();
        if (parent.Id != ParentId || parent.StartTime.ToUniversalTime().Ticks != ticks || parent.SessionId != self.SessionId
            || !MaintenancePaths.Same(parent.MainModule!.FileName, PowerShell)) throw new InvalidOperationException("Control parent identity mismatch.");
    }
    internal override Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, long> started)
        => SimulateNativeFailure ? base.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "exit 5"], started)
            : base.RunAsync(executable, arguments, started);
}
