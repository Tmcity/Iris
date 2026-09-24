using Iris.Core.Imaging;

namespace Iris.Core.Platform;

/// <param name="DeviceName">如 \\.\DISPLAY1。</param>
/// <param name="DpiScale">该显示器缩放比例，1.0 = 96 DPI。</param>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool IsPrimary);

public sealed record ForegroundAppInfo(string ProcessName, int ProcessId, string? WindowTitle, nint WindowHandle);

/// <summary>桌面环境查询（Platform.Windows 实现）。</summary>
public interface IDesktopContext
{
    ForegroundAppInfo? GetForegroundApp();

    PixelPoint GetCursorPosition();

    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>包含该点的显示器；不在任何显示器内时返回最近的显示器。</summary>
    MonitorInfo GetMonitorAt(PixelPoint point);
}

/// <summary>开机自启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run，值名 "Iris"）。</summary>
public interface IAutoStartService
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}
