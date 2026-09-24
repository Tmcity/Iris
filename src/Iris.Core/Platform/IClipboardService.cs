using Iris.Core.Imaging;

namespace Iris.Core.Platform;

/// <summary>剪贴板（Win32 实现，OpenClipboard 失败按 10 次 × 30ms 重试，仍失败抛 IrisException(ClipboardBusy)）。线程安全。</summary>
public interface IClipboardService
{
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    Task SetImageAsync(ImageData image, CancellationToken cancellationToken = default);

    uint GetSequenceNumber();
}
