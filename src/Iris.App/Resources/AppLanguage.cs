using Iris.Core.Localization;

namespace Iris.App.Resources;

internal static class AppLanguage
{
    internal static string Name => UiLanguage.Name;
    internal static void Select(string? value) => UiLanguage.Select(value);
    internal static IDisposable BeginRun() => UiLanguage.BeginRun();
}
