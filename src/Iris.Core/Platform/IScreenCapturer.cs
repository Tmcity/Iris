using Iris.Core.Imaging;

namespace Iris.Core.Platform;

/// <summary>整个虚拟桌面的一帧原始像素（BGRA32，物理像素，行跨度 Stride）。</summary>
/// <param name="VirtualBounds">虚拟桌面范围（左上角可能为负坐标）。</param>
public sealed record CapturedFrame(
    byte[] Bgra32Pixels,
    int Width,
    int Height,
    int Stride,
    PixelRect VirtualBounds,
    IReadOnlyList<MonitorInfo> Monitors,
    DateTimeOffset CapturedAt);

/// <summary>屏幕抓取（Platform.Windows 实现：GDI BitBlt，进程须为 Per-Monitor V2 DPI 感知）。</summary>
public interface IScreenCapturer
{
    Task<CapturedFrame> CaptureVirtualScreenAsync(CancellationToken cancellationToken);

    /// <param name="region">虚拟桌面坐标；与 VirtualBounds 求交后裁剪，交集为空抛 IrisException(CaptureFailed)。</param>
    ImageData Crop(CapturedFrame frame, PixelRect region, ImageFormat format = ImageFormat.Png);
}
