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
var rows = new List<object>();
var keyboardRows = new List<object>();
var failures = new List<object>();
foreach (var obj in runtime.Heap.EnumerateObjects())
{
    if (obj.IsValid && obj.Type?.Name == "Iris.Platform.Windows.Clipboard.ClipboardSnapshotService+NativeFailure")
    {
        string? message = obj.ReadStringField("_message", 160);
        if (message?.StartsWith("CLIPBOARD_", StringComparison.Ordinal) == true)
            failures.Add(new { SafeNativeCode = message, HResult = obj.ReadField<int>("_HResult") });
    }
    if (obj.IsValid && obj.Type?.Name == "Iris.Platform.Windows.Selection.KeyboardSimulator")
    {
        var task = obj.ReadObjectField("_activeCopyTask");
        if (!task.IsNull)
        {
            var result = task.ReadObjectField("m_result");
            if (!result.IsNull) keyboardRows.Add(new { Outcome = result.ReadField<int>("<Outcome>k__BackingField"), MayHaveCopied = result.ReadField<bool>("<MayHaveCopied>k__BackingField") });
        }
    }
    if (!obj.IsValid || obj.Type?.Name != "Iris.Platform.Windows.Selection.CompositeSelectionReader") continue;
    var readTask = obj.ReadObjectField("_readTask");
    if (!readTask.IsNull)
    {
        var properties = readTask.ReadObjectField("m_contingentProperties");
        if (!properties.IsNull)
        {
            var holder = properties.ReadObjectField("m_exceptionsHolder");
            if (!holder.IsNull)
            {
                var exceptions = holder.ReadObjectField("m_faultExceptions");
                if (!exceptions.IsNull)
                {
                    var items = exceptions.ReadObjectField("_items").AsArray();
                    int count = Math.Min(exceptions.ReadField<int>("_size"), 4);
                    for (int index = 0; index < count; index++)
                    {
                        var exception = items.GetObjectValue(index).ReadObjectField("_exception");
                        if (exception.IsNull || exception.Type?.Name != "Iris.Core.Errors.IrisException") continue;
                        string? message = exception.ReadStringField("_message", 160);
                        if (message?.StartsWith("SELECTION_", StringComparison.Ordinal) == true)
                            failures.Add(new { SafeStageCode = message, HResult = exception.ReadField<int>("_HResult") });
                    }
                }
            }
        }
    }
    var current = obj.ReadObjectField("_current");
    if (current.IsNull) { rows.Add(new { CurrentPresent = false }); continue; }
    var permit = current.ReadValueTypeField("<Permit>k__BackingField");
    var candidate = current.ReadValueTypeField("<Candidate>k__BackingField");
    var clear = current.ReadObjectField("<Clear>k__BackingField");
    var restore = current.ReadObjectField("<Restore>k__BackingField");
    var source = current.ReadObjectField("<Source>k__BackingField");
    rows.Add(new {
        CurrentPresent = true, SourcePresent = !source.IsNull,
        SourcePid = source.IsNull ? 0 : source.ReadField<int>("<ProcessId>k__BackingField"),
        PermitPresent = permit.ReadField<bool>("hasValue"),
        SnapshotPresent = !current.ReadObjectField("<Snapshot>k__BackingField").IsNull,
        CandidatePresent = candidate.ReadField<bool>("hasValue"),
        ClearVersionPresent = !clear.IsNull && clear.ReadValueTypeField("<Version>k__BackingField").ReadField<bool>("hasValue"),
        RestoreVersionPresent = !restore.IsNull && restore.ReadValueTypeField("<Version>k__BackingField").ReadField<bool>("hasValue"),
        Restored = current.ReadField<bool>("<Restored>k__BackingField"),
        TargetChanged = current.ReadField<bool>("<TargetChanged>k__BackingField")
    });
}
string path = Path.Combine(@"C:\IrisT040Output", "selection-copy-state-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
File.WriteAllText(path, JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Pid = pid, Rows = rows, Keyboard = keyboardRows, Failures = failures,
    TargetSuspended = false, GcRequested = false, UserTextOrCredentialsRead = false, ProductOperationPerformed = false,
    Scope = "Owned current reader transaction metadata only; no clipboard contents read." }));
Console.WriteLine(path);
