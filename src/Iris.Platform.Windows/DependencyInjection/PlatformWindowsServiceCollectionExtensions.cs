using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Platform.Windows.Capture;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Hotkey;
using Iris.Platform.Windows.Security;
using Iris.Platform.Windows.Selection;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Iris.Platform.Windows.DependencyInjection;

public static class PlatformWindowsServiceCollectionExtensions
{
    /// <summary>注册 Platform.Windows 层服务。调用方须事先注册 IAppPaths 与日志。</summary>
    public static IServiceCollection AddIrisPlatformWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<IClipboardService, Win32ClipboardService>();
        services.AddSingleton<IDesktopContext, DesktopContext>();
        services.AddSingleton<IAutoStartService, AutoStartService>();
        services.AddSingleton<INativeHotkeyAdapter>(NativeHotkeyAdapter.Instance);
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.TryAddSingleton<INativeKeyboardHookAdapter>(NativeKeyboardHookAdapter.Instance);
        services.TryAddSingleton<IHotkeyCaptureService, HotkeyCaptureService>();
        services.TryAddSingleton<INativeKeyboardInputAdapter>(Win32KeyboardInputAdapter.Instance);
        services.TryAddSingleton<KeyboardSimulator>();
        return services;
    }
}

