using Iris.Platform.Windows.Maintenance;
using Velopack;
using Velopack.Logging;

namespace Iris.App.Updates;

internal static class VelopackStartup
{
    private static readonly HashSet<string> Hooks = new(StringComparer.OrdinalIgnoreCase)
    {
        "--veloapp-install", "--veloapp-updated", "--veloapp-obsolete", "--veloapp-uninstall",
        "--squirrel-install", "--squirrel-updated", "--squirrel-obsolete", "--squirrel-uninstall"
    };
    internal static bool IsHookRequest(string[] args) => args.Length > 0
        && (args[0].StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase) || args[0].StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase));
    internal static bool IsValidHook(string[] args) => args.Length == 2 && Hooks.Contains(args[0]) && ReleaseIdentity.IsVersion(args[1]);
    internal static int RunHook(string[] args)
    {
        if (!IsValidHook(args)) return 2;
        VelopackApp.Build().SetAutoApplyOnStartup(false).SetLogger(new NullVelopackLogger()).SetArgs(args)
            .OnAfterUpdateFastCallback(_ => MaintenanceRegistration.Configure(MaintenancePaths.InstallRoot)).Run();
        return 0;
    }
    internal static void RunPrimary() => VelopackApp.Build().SetAutoApplyOnStartup(false)
        .SetLogger(new NullVelopackLogger()).SetArgs([]).Run();
}
