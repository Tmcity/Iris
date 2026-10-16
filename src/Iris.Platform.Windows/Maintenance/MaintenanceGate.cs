using System.Text.Json;
using Iris.Core.Errors;

namespace Iris.Platform.Windows.Maintenance;

internal sealed record MaintenanceTransaction(Guid Id, string Kind, string Phase, int OwnerPid, long OwnerStartedUtcTicks,
    string Version, bool DeleteData, int? WorkerPid = null, long? WorkerStartedUtcTicks = null, string? WorkerPath = null, int? WorkerExitCode = null)
{
    public override string ToString() => "MaintenanceTransaction";
}

/// <summary>A file lock covers the transaction; the durable record seals startup even after an owner crash.</summary>
internal sealed class MaintenanceGate : IDisposable
{
    private readonly string _record;
    private readonly FileStream _lock;
    private bool _disposed;
    private MaintenanceGate(string record, FileStream lease) { _record = record; _lock = lease; }
    internal static bool IsBlocked(string stateRoot)
    {
        MaintenancePaths.RequireNoLinks(stateRoot);
        if (!Directory.Exists(stateRoot)) return false;
        var record = Path.Combine(stateRoot, "transaction.json"); var lockPath = Path.Combine(stateRoot, "transaction.lock");
        MaintenancePaths.RequireNoLinks(record); MaintenancePaths.RequireNoLinks(lockPath);
        if (File.Exists(record)) return true;
        if (!File.Exists(lockPath)) return false;
        try { using var file = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    internal static MaintenanceGate Acquire(string stateRoot, bool recovery = false)
    {
        MaintenancePaths.RequireNoLinks(stateRoot); Directory.CreateDirectory(stateRoot);
        string lockPath = Path.Combine(stateRoot, "transaction.lock"); MaintenancePaths.RequireNoLinks(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            string record = Path.Combine(stateRoot, "transaction.json"); MaintenancePaths.RequireNoLinks(record);
            if (!recovery && File.Exists(record)) throw new IrisException(ErrorCode.StorageFailure, ErrorMessages.GetDefault(ErrorCode.StorageFailure));
            return new(record, lease);
        }
        catch { lease.Dispose(); throw; }
    }
    internal MaintenanceTransaction? Read()
    {
        if (!File.Exists(_record)) return null;
        MaintenancePaths.RequireNoLinks(_record);
        if (new FileInfo(_record).Length > 8192) throw ReleaseIdentity.Invalid();
        return ReleaseIdentity.Parse<MaintenanceTransaction>(File.ReadAllBytes(_record));
    }
    internal void Write(MaintenanceTransaction value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string temporary = _record + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, value, ReleaseIdentity.Json); output.Flush(true); }
            File.Move(temporary, _record, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void Complete() { ObjectDisposedException.ThrowIf(_disposed, this); File.Delete(_record); }
    public void Dispose() { if (_disposed) return; _lock.Dispose(); _disposed = true; }
}
