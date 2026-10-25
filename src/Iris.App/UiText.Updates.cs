namespace Iris.App;

internal static partial class UiText
{
    public static string MaintenanceBusy => global::Iris.App.Resources.AppResources.Get("MaintenanceBusy");
    public static string UpdateTitle => global::Iris.App.Resources.AppResources.Get("UpdateTitle");
    public static string UpdatePreference => global::Iris.App.Resources.AppResources.Get("UpdatePreference");
    public static string UpdateCheck => global::Iris.App.Resources.AppResources.Get("UpdateCheck");
    public static string UpdateDownload => global::Iris.App.Resources.AppResources.Get("UpdateDownload");
    public static string UpdateCancel => global::Iris.App.Resources.AppResources.Get("UpdateCancel");
    public static string UpdateInstall => global::Iris.App.Resources.AppResources.Get("UpdateInstall");
    public static string UpdateInstallConfirm => global::Iris.App.Resources.AppResources.Get("UpdateInstallConfirm");
    public static string UpdateUnsupported => global::Iris.App.Resources.AppResources.Get("UpdateUnsupported");
    public static string UpdateIdle => global::Iris.App.Resources.AppResources.Get("UpdateIdle");
    public static string UpdateChecking => global::Iris.App.Resources.AppResources.Get("UpdateChecking");
    public static string UpdateNoRelease => global::Iris.App.Resources.AppResources.Get("UpdateNoRelease");
    public static string UpdateUpToDate => global::Iris.App.Resources.AppResources.Get("UpdateUpToDate");
    public static string UpdateAvailable => global::Iris.App.Resources.AppResources.Get("UpdateAvailable");
    public static string UpdateDownloading => global::Iris.App.Resources.AppResources.Get("UpdateDownloading");
    public static string UpdateReady => global::Iris.App.Resources.AppResources.Get("UpdateReady");
    public static string UpdateCancelling => global::Iris.App.Resources.AppResources.Get("UpdateCancelling");
    public static string UpdateCleanupPending => global::Iris.App.Resources.AppResources.Get("UpdateCleanupPending");
    public static string UpdateFailed => global::Iris.App.Resources.AppResources.Get("UpdateFailed");
    public static string UpdateStopped => global::Iris.App.Resources.AppResources.Get("UpdateStopped");
    public static string UpdateRecoveryRequired => global::Iris.App.Resources.AppResources.Get("UpdateRecoveryRequired");
    public static string UpdateRecoveryRetry => global::Iris.App.Resources.AppResources.Get("UpdateRecoveryRetry");
    public static string UpdateRecoveryCancel => global::Iris.App.Resources.AppResources.Get("UpdateRecoveryCancel");
    public static string UpdateRecoveryCleanupPending => global::Iris.App.Resources.AppResources.Get("UpdateRecoveryCleanupPending");
    public static string ShutdownIncomplete => global::Iris.App.Resources.AppResources.Get("ShutdownIncomplete");
    internal static string UpdateState(Updates.UpdatePhase phase) => global::Iris.App.Resources.AppResources.Get("Update" + phase);
}
