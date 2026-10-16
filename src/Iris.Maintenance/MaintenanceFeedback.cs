namespace Iris.Maintenance;

internal static class MaintenanceFeedback
{
    internal static bool RequiresWindow(bool silent, bool handoff, Exception error)
        => !silent && (!handoff || error is CommittedUpdateException);

    internal static async Task<int> ReportFailureAsync(string stage, bool silent, bool handoff, Exception error)
    {
        try { await MaintenanceResult.WriteFailureAsync(stage, error).ConfigureAwait(false); }
        catch (Exception) { /* Reporting cannot replace the primary failure or turn it into success. */ }
        if (RequiresWindow(silent, handoff, error)) MaintenanceUi.Failed();
        return 2;
    }
}
