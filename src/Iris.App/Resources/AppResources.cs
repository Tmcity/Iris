using System;
using System.Globalization;
using System.Resources;

namespace Iris.App.Resources;

/// <summary>
/// App 字符串资源访问器，优先读取嵌入资源，遇异常或缺失安全回退到静态默认文案。
/// </summary>
internal static class AppResources
{
    private static readonly ResourceManager ResourceManager = new("Iris.App.Resources.Strings", typeof(AppResources).Assembly);

    public static string AppName => GetString(nameof(AppName), UiText.AppName);

    public static string ActionNotAvailable => GetString(nameof(ActionNotAvailable), UiText.ActionNotAvailable);

    private static string GetString(string name, string fallback)
    {
        try
        {
            return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
