using Iris.Core.Imaging;
using Iris.Platform.Windows.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Iris.Platform.Windows.Imaging;

internal readonly record struct ImageDecoderHeader(ImageFormat Format, uint Width, uint Height);
internal interface IImageDecoderIo
{
    Task WriteAsync(byte[] bytes);
    Task<ImageDecoderHeader> OpenAsync();
    Task<byte[]> PixelsAsync(int width, int height);
    ValueTask DisposeWriterAsync();
    ValueTask DisposeStreamAsync();
}
internal interface IImagePreparationFactory
{
    IImageDecoderIo CreateDecoder();
    IScreenEncoderIo CreateEncoder();
    // A batch boundary for cancellation/stop observation; production has no extra work here.
    void CheckFormBudgetBatch() { }
}
internal sealed class WinRtImagePreparationFactory : IImagePreparationFactory
{
    public IImageDecoderIo CreateDecoder() => new WinRtImageDecoderIo();
    public IScreenEncoderIo CreateEncoder() => new WinRtScreenEncoderIo();
}
internal sealed class WinRtImageDecoderIo : IImageDecoderIo, IAsyncDisposable
{
    private InMemoryRandomAccessStream? _stream;
    private DataWriter? _writer;
    private BitmapDecoder? _decoder;
    public async Task WriteAsync(byte[] bytes)
    {
        _stream = new InMemoryRandomAccessStream();
        _writer = new DataWriter(_stream);
        _writer.WriteBytes(bytes);
        uint written = await _writer.StoreAsync().AsTask().ConfigureAwait(false);
        if (written != bytes.Length) throw ImagePreparationRules.Fail("INPUT_SHORT_WRITE");
        _writer.DetachStream();
        _stream.Seek(0);
    }
    public async Task<ImageDecoderHeader> OpenAsync()
    {
        _decoder = await BitmapDecoder.CreateAsync(_stream).AsTask().ConfigureAwait(false);
        var id = _decoder.DecoderInformation.CodecId;
        var format = id == BitmapDecoder.PngDecoderId ? ImageFormat.Png : id == BitmapDecoder.JpegDecoderId ? ImageFormat.Jpeg :
            id == BitmapDecoder.BmpDecoderId ? ImageFormat.Bmp : (ImageFormat)(-1);
        return new(format, _decoder.PixelWidth, _decoder.PixelHeight);
    }
    public async Task<byte[]> PixelsAsync(int width, int height)
    {
        var transform = new BitmapTransform { ScaledWidth = checked((uint)width), ScaledHeight = checked((uint)height), InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await _decoder!.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
        return pixels.DetachPixelData();
    }
    public ValueTask DisposeWriterAsync() { _writer?.Dispose(); _writer = null; return ValueTask.CompletedTask; }
    public ValueTask DisposeStreamAsync() { _stream?.Dispose(); _stream = null; _decoder = null; return ValueTask.CompletedTask; }
    public async ValueTask DisposeAsync()
    {
        var errors = new List<string>();
        try { await DisposeWriterAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ImagePreparationRules.Describe("DECODER_WRITER", ex)); }
        try { await DisposeStreamAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ImagePreparationRules.Describe("DECODER_STREAM", ex)); }
        if (errors.Count != 0) throw new ImagePreparationException(Iris.Core.Errors.ErrorCode.Unknown, string.Join(';', errors));
    }
}
