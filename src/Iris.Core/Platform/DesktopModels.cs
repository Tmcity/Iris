using Iris.Core.Imaging;

namespace Iris.Core.Platform;

/// <summary>显示器只读快照；Bounds/WorkArea 均为虚拟桌面物理像素，可含负坐标。</summary>
/// <param name="DeviceName">当前运行时设备名，如 \\.\DISPLAY1；不保证跨硬件变更稳定。</param>
/// <param name="DpiScale">本显示器 DPI/96.0，有限且大于零；1.0 = 96 DPI。</param>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool IsPrimary);

/// <summary>短暂的前台窗口快照；后续输入操作须重新确认目标。</summary>
/// <param name="ProcessName">映像文件名，含实际扩展名（通常 .exe）、不含路径；比较使用 OrdinalIgnoreCase。</param>
/// <param name="WindowTitle">可空的顶层标题，最多 1,024 字符；不作为日志内容。</param>
public sealed record ForegroundAppInfo(string ProcessName, int ProcessId, string? WindowTitle, nint WindowHandle);

/// <summary>线程安全的桌面环境查询（Platform.Windows）；完整规则见 DESKTOP_CONTEXT.md。
/// 构造不访问桌面；每次采集清理自身资源并恢复线程 DPI context；不变更显示配置或激活窗口。</summary>
public interface IDesktopContext
{
    /// <summary>取得前台快照；无窗口、来源不可读或两次采样仍不稳定时返回 null。标题可空，不影响已识别进程。</summary>
    ForegroundAppInfo? GetForegroundApp();

    /// <summary>取得光标物理像素坐标；失败抛 IrisException(Unknown)。不移动光标。</summary>
    PixelPoint GetCursorPosition();

    /// <summary>返回新建的只读显示器快照，排除未接入桌面及镜像伪设备，按主屏优先、DeviceName Ordinal 排序。
    /// 不一致快照最多重采一次；仍失败或没有有效显示器抛 IrisException(Unknown)，不伪造默认屏幕/DPI。</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>从同一有效快照选择包含该点或距该点最近的显示器；采用点到矩形所含整数像素的平方距离。
    /// 并列优先主屏，再按 DeviceName Ordinal；边界为半开区间，极端坐标运算不溢出。</summary>
    MonitorInfo GetMonitorAt(PixelPoint point);
}

/// <summary>开机自启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run，值名 "Iris"）。</summary>
public interface IAutoStartService
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}
