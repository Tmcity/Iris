using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Owned T-040 Sandbox only.");
if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) throw new ArgumentException("Expected owned Iris PID.");
using var process = Process.GetProcessById(pid);
string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iris", "current", "Iris.App.exe");
if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected memory observation target.");
string root = @"C:\IrisT040Output";
if (!Directory.Exists(root)) throw new InvalidOperationException("Missing owned output mapping.");
process.Refresh();
long before = process.WorkingSet64;
long privateBytes = process.PrivateMemorySize64;
int pageSize = Environment.SystemPageSize;
int infoSize = Marshal.SizeOf<WorkingSetInfo>();
var groups = new Dictionary<(nint Allocation, uint Type, string Path), MemoryGroup>();
long address = 0;
int committedRegions = 0;
while (Native.VirtualQueryEx(process.Handle, (nint)address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) != 0)
{
    long next = checked(region.BaseAddress.ToInt64() + (long)region.RegionSize);
    if (next <= address) break;
    if (region.State == 0x1000)
    {
        committedRegions++;
        string path = "";
        if (region.Type != 0x20000)
        {
            char[] chars = new char[4096];
            uint length = Native.GetMappedFileNameW(process.Handle, region.BaseAddress, chars, (uint)chars.Length);
            if (length > 0) path = new string(chars, 0, (int)length);
        }
        var key = (region.AllocationBase, region.Type, path);
        if (!groups.TryGetValue(key, out var group)) { group = new MemoryGroup(region.AllocationBase.ToInt64(), region.Type, path); groups.Add(key, group); }
        group.CommittedBytes += (long)region.RegionSize;
        for (long page = region.BaseAddress.ToInt64(); page < next;)
        {
            int count = (int)Math.Min(4096, (next - page + pageSize - 1) / pageSize);
            var infos = new WorkingSetInfo[count];
            for (int i = 0; i < count; i++) infos[i].Address = (nint)(page + (long)i * pageSize);
            if (!Native.QueryWorkingSetEx(process.Handle, infos, checked((uint)(count * infoSize)))) throw new Win32Exception(Marshal.GetLastWin32Error());
            foreach (var info in infos)
            {
                ulong flags = info.Flags.ToUInt64();
                if ((flags & 1) == 0) continue;
                group.ResidentBytes += pageSize;
                group.ResidentBytesByProtection[region.Protect] = group.ResidentBytesByProtection.GetValueOrDefault(region.Protect) + pageSize;
                if ((flags & (1UL << 15)) != 0) group.SharedResidentBytes += pageSize;
                else group.PrivateResidentBytes += pageSize;
            }
            page += (long)count * pageSize;
        }
    }
    address = next;
}
process.Refresh();
long after = process.WorkingSet64;
var totals = groups.Values.GroupBy(g => g.Type).Select(g => new {
    Type = g.Key == 0x1000000 ? "Image" : g.Key == 0x40000 ? "Mapped" : g.Key == 0x20000 ? "Private" : "Other",
    ResidentBytes = g.Sum(x => x.ResidentBytes), SharedResidentBytes = g.Sum(x => x.SharedResidentBytes), PrivateResidentBytes = g.Sum(x => x.PrivateResidentBytes)
}).ToArray();
var result = new {
    Utc = DateTimeOffset.UtcNow, Pid = pid, ProductOperationPerformed = false, WorkingSetTrimmed = false, GcRequested = false,
    WorkingSetBefore = before, WorkingSetAfter = after, PrivateBytesBefore = privateBytes,
    ObservedResidentBytes = totals.Sum(t => t.ResidentBytes), PageBytes = pageSize, CommittedRegions = committedRegions,
    Totals = totals, RegionsByAllocation = groups.Values.OrderByDescending(g => g.ResidentBytes).ToArray(),
    Scope = "VirtualQueryEx and QueryWorkingSetEx metadata only; no memory contents read or process code executed."
};
string destination = Path.Combine(root, "memory-map-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + ".json");
await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(result)).ConfigureAwait(false);

internal sealed class MemoryGroup(long allocation, uint type, string path)
{
    public long AllocationBase { get; } = allocation;
    public uint Type { get; } = type;
    public string Path { get; } = path;
    public long CommittedBytes { get; set; }
    public long ResidentBytes { get; set; }
    public long SharedResidentBytes { get; set; }
    public long PrivateResidentBytes { get; set; }
    public Dictionary<uint, long> ResidentBytesByProtection { get; } = [];
}
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryRegion
{
    public nint BaseAddress;
    public nint AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}
[StructLayout(LayoutKind.Sequential)]
internal struct WorkingSetInfo { public nint Address; public nuint Flags; }
internal static class Native
{
    [DllImport("kernel32.dll", SetLastError=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nuint VirtualQueryEx(nint process, nint address, out MemoryRegion info, nuint length);
    [DllImport("psapi.dll", SetLastError=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryWorkingSetEx(nint process, [In, Out] WorkingSetInfo[] infos, uint length);
    [DllImport("psapi.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetMappedFileNameW(nint process, nint address, [Out] char[] fileName, uint length);
}
