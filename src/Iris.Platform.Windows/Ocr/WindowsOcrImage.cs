using Iris.Core.Errors;
using Iris.Platform.Windows.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Iris.Platform.Windows.Ocr;

internal interface IWindowsOcrImageFactory { IWindowsOcrImage Create(); }
internal interface IWindowsOcrImage
{
    Task WriteAsync(byte[] bytes);
    Task<ImageDecoderHeader> HeaderAsync();
    Task<SoftwareBitmap> BitmapAsync();
    ValueTask DisposeBitmapAsync();
    ValueTask DisposeWriterAsync();
    ValueTask DisposeStreamAsync();
}
internal sealed class WindowsOcrImageFactory : IWindowsOcrImageFactory
{
    public IWindowsOcrImage Create() => new WindowsOcrImage();
}
internal sealed class WindowsOcrImage : IWindowsOcrImage, IAsyncDisposable
{
    private InMemoryRandomAccessStream? _stream;
    private DataWriter? _writer;
    private BitmapDecoder? _decoder;
    private SoftwareBitmap? _bitmap;
    public async Task WriteAsync(byte[] bytes)
    {
        _stream = new(); _writer = new(_stream); _writer.WriteBytes(bytes);
        uint count = await _writer.StoreAsync().AsTask().ConfigureAwait(false);
        if (count != bytes.Length) throw new WindowsOcrException(ErrorCode.Unknown, "SHORT_WRITE");
        _writer.DetachStream(); _stream.Seek(0);
    }
    public async Task<ImageDecoderHeader> HeaderAsync()
    {
        _decoder = await BitmapDecoder.CreateAsync(_stream).AsTask().ConfigureAwait(false);
        var codec = _decoder.DecoderInformation.CodecId;
        var format = codec == BitmapDecoder.PngDecoderId ? Iris.Core.Imaging.ImageFormat.Png :
            codec == BitmapDecoder.BmpDecoderId ? Iris.Core.Imaging.ImageFormat.Bmp : (Iris.Core.Imaging.ImageFormat)(-1);
        return new(format, _decoder.PixelWidth, _decoder.PixelHeight);
    }
    public async Task<SoftwareBitmap> BitmapAsync()
    {
        _bitmap = await _decoder!.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
        return _bitmap;
    }
    public ValueTask DisposeBitmapAsync() { _bitmap?.Dispose(); _bitmap = null; return ValueTask.CompletedTask; }
    public ValueTask DisposeWriterAsync() { _writer?.Dispose(); _writer = null; return ValueTask.CompletedTask; }
    public ValueTask DisposeStreamAsync() { _stream?.Dispose(); _stream = null; _decoder = null; return ValueTask.CompletedTask; }
    public async ValueTask DisposeAsync()
    {
        var errors = new List<string>();
        try { await DisposeBitmapAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("BITMAP", ex)); }
        try { await DisposeWriterAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("WRITER", ex)); }
        try { await DisposeStreamAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("STREAM", ex)); }
        if (errors.Count != 0) throw new WindowsOcrException(ErrorCode.Unknown, "IMAGE_CLEANUP", diagnostics: string.Join(';', errors));
    }
}
