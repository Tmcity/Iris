using System.Globalization;
using System.Resources;

namespace Iris.Core.Localization;

internal static class CoreText
{
    private static readonly ResourceManager Manager = new("Iris.Core.Localization.Strings", typeof(CoreText).Assembly);
    internal static string Get(string key)
    {
        try { return Manager.GetString(key, UiLanguage.Culture)?.ReplaceLineEndings("\n") ?? Fallback(); }
        catch (MissingManifestResourceException) { return "An unexpected error occurred."; }
    }
    private static string Fallback() => Manager.GetString("ErrorUnknown", UiLanguage.Culture) ?? "An unexpected error occurred.";
    internal static string Format(string key, params object?[] arguments)
    {
        try { return string.Format(CultureInfo.InvariantCulture, Get(key), arguments); }
        catch (FormatException) { return Get("ErrorUnknown"); }
    }
}
