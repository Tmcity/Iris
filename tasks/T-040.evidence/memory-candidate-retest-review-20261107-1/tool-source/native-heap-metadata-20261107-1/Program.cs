using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned T-040 Sandbox only.");
if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) throw new ArgumentException("Expected owned Iris PID.");
using var process = Process.GetProcessById(pid);
string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "current", "Iris.App.exe");
if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected target.");
string root = @"C:\IrisT040Output";
if (!Directory.Exists(root)) throw new InvalidOperationException("Missing owned output mapping.");
using var target = DataTarget.AttachToProcess(pid, suspend: false);
string dac = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.14\mscordaccore.dll";
using var runtime = target.ClrVersions.Single().CreateRuntime(dac);
var ranges = runtime.EnumerateClrNativeHeaps().Select(heap => new
{
    Start = heap.MemoryRange.Start,
    End = heap.MemoryRange.End,
    Bytes = heap.MemoryRange.Length,
    Kind = heap.Kind.ToString(),
    State = heap.State.ToString(),
    heap.GCHeap,
}).ToArray();
var summary = ranges.GroupBy(range => range.Kind).Select(group => new
{
    Kind = group.Key,
    Count = group.Count(),
    Bytes = group.Aggregate(0UL, (sum, range) => sum + range.Bytes),
}).OrderByDescending(group => group.Bytes).ToArray();
var dynamicNames = new Dictionary<string, int>(StringComparer.Ordinal);
foreach (var obj in runtime.Heap.EnumerateObjects())
{
    if (!obj.IsValid || obj.Type?.Name != "System.Reflection.Emit.DynamicMethod") continue;
    string name = obj.ReadStringField("_name") ?? "<unnamed>";
    // These are generated code identifiers, never request or clipboard string fields.
    dynamicNames[name] = dynamicNames.GetValueOrDefault(name) + 1;
}
var data = new
{
    Utc = DateTimeOffset.UtcNow,
    Pid = pid,
    TargetSuspended = false,
    GcRequested = false,
    WorkingSetTrimmed = false,
    ProductOperationPerformed = false,
    UserTextOrCredentialsExported = false,
    Ranges = ranges,
    Summary = summary,
    DynamicMethodCodeNames = dynamicNames.OrderByDescending(item => item.Value).Select(item => new { Name = item.Key, Count = item.Value }).ToArray(),
};
string path = Path.Combine(root, "native-heap-metadata-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data)).ConfigureAwait(false);
