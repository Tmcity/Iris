using System.Globalization;
using System.Resources;
using System.Text;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Maintenance;

internal static class MaintenanceUi
{
    private static readonly ResourceManager Text = new("Iris.Maintenance.Strings", typeof(MaintenanceUi).Assembly);
    internal static string Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en-US" : "zh-CN";
    internal static string Get(string name) => Text.GetString(name, CultureInfo.GetCultureInfo(Language)) ?? "Iris";
    internal static bool ConfirmInstall() => PInvoke.MessageBox(default, Get("InstallConfirm"), "Iris", MESSAGEBOX_STYLE.MB_OKCANCEL | MESSAGEBOX_STYLE.MB_ICONINFORMATION) == MESSAGEBOX_RESULT.IDOK;
    // Yes preserves data (the default); No deletes only after a successful uninstall; Cancel has no side effects.
    internal static int UninstallChoice() => (int)PInvoke.MessageBox(default, Get("UninstallChoice"), "Iris", MESSAGEBOX_STYLE.MB_YESNOCANCEL | MESSAGEBOX_STYLE.MB_ICONQUESTION);
    internal static bool Retry() => PInvoke.MessageBox(default, Get("Retry"), "Iris", MESSAGEBOX_STYLE.MB_RETRYCANCEL | MESSAGEBOX_STYLE.MB_ICONWARNING) == MESSAGEBOX_RESULT.IDRETRY;
    internal static void Failed() => PInvoke.MessageBox(default, string.Format(CultureInfo.InvariantCulture, CompositeFormat.Parse(Get("Failed")), MaintenanceCopy.RecoveryPath), "Iris", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR);
    internal static void Completed() => PInvoke.MessageBox(default, Get("Completed"), "Iris", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
}
