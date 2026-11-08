using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned Sandbox only.");
if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) throw new ArgumentException("Owned primary PID required.");
using var process = Process.GetProcessById(pid);
string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "current", "Iris.App.exe");
if (process.MainModule?.FileName != expected) throw new InvalidOperationException("Unexpected primary.");
using var target = DataTarget.AttachToProcess(pid, suspend: false);
using var runtime = target.ClrVersions.Single().CreateRuntime(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.14\mscordaccore.dll");
var sessions = new List<object>();
foreach (var obj in runtime.Heap.EnumerateObjects())
{
    if (!obj.IsValid || obj.Type?.Name != "Iris.Platform.Windows.Hotkey.HotkeyCaptureSession") continue;
    sessions.Add(new
    {
        IsReady = obj.ReadField<bool>("_isReady"),
        IsStopped = obj.ReadField<bool>("_isStopped"),
        HookHandle = obj.ReadField<ulong>("_hookHandle"),
        ThreadId = obj.ReadField<uint>("_threadId"),
        IsHookUnhooked = obj.ReadField<bool>("_isHookUnhooked"),
        InitialHeldCount = Count(obj.ReadObjectField("_heldKeysAtStart")),
        HeldModifierCount = Count(obj.ReadObjectField("_heldModifiers")),
        ActiveModifiers = obj.ReadField<int>("_activeModifiers"),
        CandidateKeyVk = obj.ReadField<uint>("_candidateKeyVk"),
        CandidateModifiers = obj.ReadField<int>("_candidateModifiers")
    });
}
string path = Path.Combine(@"C:\IrisT040Output", "hotkey-state-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
File.WriteAllText(path, JsonSerializer.Serialize(new
{
    Utc = DateTimeOffset.UtcNow, Pid = pid, Sessions = sessions,
    TargetSuspended = false, GcRequested = false, UserTextOrCredentialsRead = false,
    ForegroundInputInjected = false, ProductOperationPerformed = false,
    Scope = "Owned hotkey readiness and primitive key-state metadata only."
}));
Console.WriteLine(path);

static int Count(ClrObject value) => value.IsNull ? 0 : value.ReadField<int>("_count") - value.ReadField<int>("_freeCount");
