using Iris.Core.Imaging;

namespace Iris.Core.Platform;

/// <summary>
/// 线程安全的 Win32 剪贴板服务，异步操作在后台串行执行；完整规则见 CLIPBOARD_SERVICE.md。
/// OpenClipboard 最多尝试 10 次，首次立即执行、失败之间等待 30ms；耗尽抛 IrisException(ClipboardBusy)。
/// 等待和写入提交前可取消；EmptyClipboard 成功后完成发布和清理，不以迟到取消撤销既定结果。
/// 操作完成前等待真实原生调用退出；不依赖 WPF/STA，不在持有剪贴板时 await。
/// </summary>
public interface IClipboardService
{
    /// <summary>读取 CF_UNICODETEXT；无该格式返回 null，合法空文本返回空串。按原生块边界读取，格式无效抛 InvalidResponse，超限抛 InputTooLong。</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    /// <summary>写入 CF_UNICODETEXT，保留原文；null/内嵌 NUL 抛 InvalidArgument，超过 4,194,304 个 UTF-16 code unit 抛 InputTooLong。</summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>解码 PNG/JPEG/BMP 并立即发布 CF_DIBV5；格式/尺寸矛盾抛 ImageInvalid，超出规范预算抛 ImageTooLarge。调用完成前不得修改 Bytes。</summary>
    Task SetImageAsync(ImageData image, CancellationToken cancellationToken = default);

    /// <summary>返回当前窗口站原生 uint 序列号（含 0），不打开剪贴板；可能回绕，只以相等性判断变化。</summary>
    uint GetSequenceNumber();
}
