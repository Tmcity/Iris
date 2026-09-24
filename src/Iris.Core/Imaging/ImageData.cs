namespace Iris.Core.Imaging;

public enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
}

/// <summary>已编码的图像（PNG/JPEG/BMP 字节）。跨层传递图像只用此类型，不传平台位图。</summary>
public sealed record ImageData(byte[] Bytes, ImageFormat Format, int PixelWidth, int PixelHeight);

/// <summary>OCR 服务对输入图像的限制。</summary>
/// <param name="MaxEncodedBytes">编码后最大字节数。</param>
/// <param name="MaxBytesAppliesToBase64">true 表示 MaxEncodedBytes 针对 Base64 后的长度（约为原始 4/3）。</param>
/// <param name="MaxLongSide">最长边像素上限。</param>
/// <param name="MinShortSide">最短边像素下限。</param>
public sealed record ImageConstraints(
    long MaxEncodedBytes,
    bool MaxBytesAppliesToBase64,
    int MaxLongSide,
    int MinShortSide,
    IReadOnlyList<ImageFormat> AcceptedFormats);

/// <summary>
/// OCR 前的图像预处理。规则：
/// 最短边 &lt; MinShortSide → 等比放大（放大后仍需满足 MaxLongSide，否则四周补白）；
/// 最长边 &gt; MaxLongSide → 等比缩小；格式不被接受 → 转为 AcceptedFormats[0]；
/// 超过字节上限 → 先转 JPEG，再按 90→50 逐级降低质量；仍超限抛 IrisException(ImageTooLarge)。
/// 已满足全部约束时原样返回同一实例。
/// </summary>
public interface IImagePreprocessor
{
    Task<ImageData> PrepareAsync(ImageData image, ImageConstraints constraints, CancellationToken cancellationToken);
}
