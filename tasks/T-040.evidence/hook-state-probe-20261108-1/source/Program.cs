using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned Sandbox only.");
if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) throw new ArgumentException("Owned Iris PID required.");
using var process = Process.GetProcessById(pid);
string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "current", "Iris.App.exe");
if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected target.");
using var target = DataTarget.AttachToProcess(pid, suspend: false);
using var runtime = target.ClrVersions.Single().CreateRuntime(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.14\mscordaccore.dll");
var rows = new List<object>();
foreach (var obj in runtime.Heap.EnumerateObjects())
{
    if (!obj.IsValid || obj.Type?.Name != "Iris.Platform.Windows.Hooks.MouseSelectionMonitor") continue;
    var run = obj.ReadObjectField("_run");
    ulong handle = 0;
    bool unhooked = false;
    uint threadId = 0;
    if (!run.IsNull)
    {
        threadId = run.ReadField<uint>("ThreadId");
        var hook = run.ReadObjectField("Hook");
        if (!hook.IsNull) { handle = hook.ReadField<ulong>("<Handle>k__BackingField"); unhooked = hook.ReadField<bool>("<Unhooked>k__BackingField"); }
    }
    rows.Add(new { Address = obj.Address, Running = obj.ReadField<bool>("_running"), Generation = obj.ReadField<long>("_generation"), RunPresent = !run.IsNull, HookHandle = handle, Unhooked = unhooked, ThreadId = threadId });
}
string path = Path.Combine(@"C:\IrisT040Output", "hook-state-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Pid = pid, Rows = rows, TargetSuspended = false, GcRequested = false, ProductOperationPerformed = false, UserTextOrCredentialsRead = false })).ConfigureAwait(false);
Console.WriteLine(path);
