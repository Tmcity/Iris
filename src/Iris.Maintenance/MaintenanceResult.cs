using System.Diagnostics;
using System.Text.Json;
using Iris.Platform.Windows.Maintenance;

namespace Iris.Maintenance;

/// <summary>Records only bounded failure categories and process identity, never raw exception messages.</summary>
internal static class MaintenanceResult
{
    internal static async Task WriteFailureAsync(string stage, Exception error)
    {
        string root = MaintenancePaths.StateRoot;
        MaintenancePaths.RequireNoLinks(root); Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess();
        string path = Path.Combine(root, "last-failure.json"); MaintenancePaths.RequireNoLinks(path);
        var failures = new List<object>(); var pending = new Queue<Exception>(); pending.Enqueue(error);
        while (pending.TryDequeue(out var item) && failures.Count < 16)
        {
            failures.Add(new { Type = item.GetType().Name, HResult = $"0x{item.HResult:X8}",
                RuntimeStep = item.Data[DesktopRuntimeDownload.FailureStepKey] is DesktopRuntimeStep runtimeStep ? runtimeStep.ToString() : null,
                SignatureReason = item.Data[RuntimeInstallerTrust.FailureKey] is RuntimeTrustFailure trustFailure ? trustFailure.ToString() : null,
                SignatureStatus = item.Data[RuntimeInstallerTrust.StatusKey] is int trustStatus ? $"0x{trustStatus:X8}" : null,
                RuntimeHttpStatus = item.Data[DesktopRuntimeDownload.HttpStatusKey] is int httpStatus ? (int?)httpStatus : null,
                RuntimeTransferStep = item.Data[DesktopRuntimeDownload.TransferStepKey] is RuntimeTransferStep transferStep ? transferStep.ToString() : null,
                RuntimeTransferBytes = item.Data[DesktopRuntimeDownload.TransferBytesKey] is long transferBytes ? (long?)transferBytes : null });
            if (item is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions.Take(16)) pending.Enqueue(child);
            else if (item.InnerException != null) pending.Enqueue(item.InnerException);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Stage = stage, Pid = process.Id, StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks, ExitCode = 2, Failures = failures });
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
    }
}
