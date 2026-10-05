using Iris.Core.Imaging;

namespace Iris.Core.Platform;

/// <summary>整个虚拟桌面的top-down BGRA32原始帧，物理像素，alpha=255，行跨度Stride。
/// Capture输出独立紧密缓冲及只读显示器快照；CropAsync真实结束前调用方不得修改输入像素。预算见SCREEN_CAPTURE.md。</summary>
/// <param name="VirtualBounds">本次显示器物理Bounds的并集，左上角可为负，宽高与帧一致。</param>
public sealed record CapturedFrame(
    byte[] Bgra32Pixels,
    int Width,
    int Height,
    int Stride,
    PixelRect VirtualBounds,
    IReadOnlyList<MonitorInfo> Monitors,
    DateTimeOffset CapturedAt)
{
    public override string ToString() => "CapturedFrame";
}

/// <summary>GDI物理屏幕捕获和后台内存编码；一次只接受一个Capture或Crop，忙时抛CaptureFailed，不排队或取消旧调用。
/// 取消须等待已发出的native/编码操作与清理；失败清理保留责任供后续请求或App退出重试。完整语义见SCREEN_CAPTURE.md。</summary>
public interface IScreenCapturer
{
    /// <summary>返回本次物理桌面帧。拓扑变化在完整清理后最多重试一次；无效几何/native失败抛CaptureFailed，超预算抛ImageTooLarge。</summary>
    Task<CapturedFrame> CaptureVirtualScreenAsync(CancellationToken cancellationToken);

    /// <summary>后台按真实Stride裁剪并编码为独立PNG/JPEG/BMP；PNG/BMP无损，JPEG质量0.9。
    /// 非法帧/格式抛ImageInvalid，超预算抛ImageTooLarge；仅完整清理后的调用方取消抛OperationCanceledException。</summary>
    /// <param name="region">虚拟桌面物理半开矩形；与VirtualBounds求交，非正尺寸或空交集抛CaptureFailed。</param>
    Task<ImageData> CropAsync(CapturedFrame frame, PixelRect region, ImageFormat format = ImageFormat.Png,
        CancellationToken cancellationToken = default);
}
