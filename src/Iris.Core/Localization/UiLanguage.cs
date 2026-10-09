using System.Globalization;

namespace Iris.Core.Localization;

/// <summary>Explicit process-run UI language; never changes numeric, protocol, or ambient thread cultures.</summary>
internal static class UiLanguage
{
    private static string? _selected;
    internal static string Name => Volatile.Read(ref _selected) ?? StartupLanguage(CultureInfo.CurrentUICulture);
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(Name);
    internal static string StartupLanguage(CultureInfo culture) => culture.TwoLetterISOLanguageName == "en" ? "en-US" : "zh-CN";
    internal static string Normalize(string? value) => string.Equals(value, "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
    internal static void Select(string? value) => Interlocked.CompareExchange(ref _selected, Normalize(value), null);

    // One scope surrounds a primary process run. Isolated tests simulate sequential runs and restore state.
    internal static IDisposable BeginRun() => new RunScope(Interlocked.Exchange(ref _selected, null));
    private sealed class RunScope(string? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _selected, previous); }
    }
}
