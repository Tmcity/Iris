using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Platform.Windows.Capture;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Hotkey;
using Iris.Platform.Windows.Hooks;
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
        services.TryAddSingleton<IClipboardSnapshotNative>(NativeClipboardSnapshotAdapter.Instance);
        services.TryAddSingleton<ClipboardSnapshotService>();
        services.AddSingleton<IDesktopContext, DesktopContext>();
        services.AddSingleton<IAutoStartService, AutoStartService>();
        services.AddSingleton<INativeHotkeyAdapter>(NativeHotkeyAdapter.Instance);
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.TryAddSingleton<INativeKeyboardHookAdapter>(NativeKeyboardHookAdapter.Instance);
        services.TryAddSingleton<IHotkeyCaptureService, HotkeyCaptureService>();
        services.TryAddSingleton<INativeKeyboardInputAdapter>(Win32KeyboardInputAdapter.Instance);
        services.TryAddSingleton<KeyboardSimulator>();
        services.TryAddSingleton<ITargetWindowValidator>(RealTargetWindowValidator.Instance);
        services.TryAddSingleton<IUiaProcessRunner>(RealUiaProcessRunner.Instance);
        services.TryAddSingleton<UiaSelectionReader>();
        services.TryAddSingleton<ICopySafetyNative>(NativeCopySafetyAdapter.Instance);
        services.TryAddSingleton<CopySafetyPolicy>();
        services.TryAddSingleton<CompositeSelectionReader>();
        services.TryAddSingleton<ISelectionTextReader>(provider => provider.GetRequiredService<CompositeSelectionReader>());
        services.TryAddSingleton<IMouseHookApi, Win32MouseHookApi>();
        services.TryAddSingleton<NativeMouseAdapter>();
        services.TryAddSingleton<IMouseThreadFactory, MouseThreadFactory>();
        services.TryAddSingleton<IMouseSelectionMonitor, MouseSelectionMonitor>();
        services.TryAddSingleton<IGdiCaptureApi, Win32GdiCaptureApi>();
        services.TryAddSingleton<NativeGdiCaptureAdapter>();
        services.TryAddSingleton<IScreenEncoderFactory, WinRtScreenEncoderFactory>();
        services.TryAddSingleton<ScreenMemoryEncoder>();
        services.TryAddSingleton<ICaptureThreadFactory, CaptureThreadFactory>();
        services.TryAddSingleton<GdiScreenCapturer>();
        services.TryAddSingleton<IScreenCapturer>(provider => provider.GetRequiredService<GdiScreenCapturer>());
        services.TryAddSingleton<Iris.Platform.Windows.Imaging.IImagePreparationFactory, Iris.Platform.Windows.Imaging.WinRtImagePreparationFactory>();
        services.TryAddSingleton<Iris.Platform.Windows.Imaging.WinRtImagePreprocessor>();
        services.TryAddSingleton<Iris.Core.Imaging.IImagePreprocessor>(provider => provider.GetRequiredService<Iris.Platform.Windows.Imaging.WinRtImagePreprocessor>());
        return services;
    }
}
