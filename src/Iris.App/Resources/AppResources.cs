using System.Globalization;
using System.Resources;
using Iris.Core.Localization;

namespace Iris.App.Resources;

internal static class AppResources
{
    private static readonly ResourceManager Manager = new("Iris.App.Resources.Strings", typeof(AppResources).Assembly);
    public static string AppName => Get(nameof(AppName));
    public static string ActionNotAvailable => Get(nameof(ActionNotAvailable));
    internal static string Get(string key)
    {
        try { return Manager.GetString(key, UiLanguage.Culture)?.ReplaceLineEndings("\n") ?? CoreText.Get("ErrorUnknown"); }
        catch (MissingManifestResourceException) { return CoreText.Get("ErrorUnknown"); }
    }
    internal static string Format(string key, params object?[] arguments)
    {
        try { return string.Format(CultureInfo.InvariantCulture, Get(key), arguments); }
        catch (FormatException) { return CoreText.Get("ErrorUnknown"); }
    }
}
