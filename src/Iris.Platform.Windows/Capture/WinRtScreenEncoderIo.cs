using Iris.Core.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Iris.Platform.Windows.Capture;

internal sealed class WinRtScreenEncoderFactory : IScreenEncoderFactory
{
    public IScreenEncoderIo Create() => new WinRtScreenEncoderIo();
}

internal sealed class WinRtScreenEncoderIo : IScreenEncoderIo, IAsyncDisposable
{
    private InMemoryRandomAccessStream? _stream;
    private BitmapEncoder? _encoder;
    private IBuffer? _buffer;
    private DataReader? _reader;
    public async Task CreateAsync(ImageFormat format, float jpegQuality)
    {
        _stream = new InMemoryRandomAccessStream();
        var codec = format switch { ImageFormat.Png => BitmapEncoder.PngEncoderId, ImageFormat.Jpeg => BitmapEncoder.JpegEncoderId, _ => BitmapEncoder.BmpEncoderId };
        if (format == ImageFormat.Jpeg)
        {
            var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(jpegQuality, PropertyType.Single) };
            _encoder = await BitmapEncoder.CreateAsync(codec, _stream, properties).AsTask().ConfigureAwait(false);
        }
        else _encoder = await BitmapEncoder.CreateAsync(codec, _stream).AsTask().ConfigureAwait(false);
    }
    public void SetPixels(byte[] pixels, int width, int height) =>
        _encoder!.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, checked((uint)width), checked((uint)height), 96, 96, pixels);
    public async Task FlushAsync() => await _encoder!.FlushAsync().AsTask().ConfigureAwait(false);
    public ulong Size => _stream!.Size;
    public async Task<uint> ReadAsync(uint count)
    {
        _stream!.Seek(0);
        _buffer = await _stream.ReadAsync(new global::Windows.Storage.Streams.Buffer(count), count, InputStreamOptions.None).AsTask().ConfigureAwait(false);
        return _buffer.Length;
    }
    public void CopyBytes(byte[] target)
    {
        _reader = DataReader.FromBuffer(_buffer);
        _reader.ReadBytes(target);
    }
    public ValueTask DisposeReaderAsync()
    {
        _reader?.Dispose(); _reader = null; _buffer = null;
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeStreamAsync()
    {
        _stream?.Dispose(); _stream = null; _encoder = null;
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        var errors = new List<string>();
        try { await DisposeReaderAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ScreenCaptureErrors.Describe("ENCODER_READER_DISPOSE", ex)); }
        try { await DisposeStreamAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ScreenCaptureErrors.Describe("ENCODER_STREAM_DISPOSE", ex)); }
        if (errors.Count != 0) throw ScreenCaptureErrors.Combine("ENCODER_DISPOSE", null, errors, false);
    }
}
