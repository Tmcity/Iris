using System.Runtime.ExceptionServices;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Capture;
using Iris.Platform.Windows.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Iris.Platform.Windows.Tests.Imaging;

internal sealed class ImagePreparationTestScope : IImagePreparationFactory
{
    internal WinRtImagePreprocessor Service { get; }
    internal readonly List<(Task Task, Type? Expected)> Tasks = [];
    internal readonly List<Action> Releases = [];
    internal readonly List<Decoder> Decoders = [];
    internal readonly List<Encoder> Encoders = [];
    internal readonly Dictionary<string, Exception> Failures = [];
    internal Func<string, Task>? Before;
    internal Func<int, ulong>? EncodedSize { get; set; }
    internal Func<int, byte[]>? EncodedBytes { get; set; }
    internal Action? FormBudgetBatch { get; set; }
    public void CheckFormBudgetBatch() => FormBudgetBatch?.Invoke();
    internal ImageDecoderHeader? Header { get; set; }
    internal uint? ReadLength { get; set; }
    internal int InFlight;
    internal ImagePreparationTestScope() { Service = new(this); }
    public IImageDecoderIo CreateDecoder() { var decoder = new Decoder(this); Decoders.Add(decoder); return decoder; }
    public IScreenEncoderIo CreateEncoder() { var encoder = new Encoder(this, Encoders.Count); Encoders.Add(encoder); return encoder; }
    internal async Task Call(string stage, Func<Task> action)
    {
        if ((stage.EndsWith("Stream", StringComparison.Ordinal) || stage.EndsWith("Writer", StringComparison.Ordinal) || stage.EndsWith("Reader", StringComparison.Ordinal)) && InFlight != 0)
            throw new InvalidOperationException("Disposed while operation in flight.");
        InFlight++;
        try
        {
            if (Before != null) await Before(stage);
            if (Failures.TryGetValue(stage, out var failure)) throw failure;
            await action();
        }
        finally { InFlight--; }
    }
    internal (Task Entered, Action Release) Gate(string stage)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Before = async s => { if (s == stage) { entered.TrySetResult(); await release.Task; } };
        Action open = () => release.TrySetResult(); Releases.Add(open); return (entered.Task, open);
    }
    internal Task<ImageData> Start(ImageData input, ImageConstraints c, Type? expected = null, CancellationToken token = default)
    {
        var task = Service.PrepareAsync(input, c, token); Tasks.Add((task, expected)); return task;
    }
    internal static ImageConstraints Constraints(long bytes = 100000, int max = 100, int min = 0, bool base64 = false, params ImageFormat[] formats) =>
        new(bytes, base64, max, min, formats.Length == 0 ? [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp] : formats);
    internal static async Task Run(Func<ImagePreparationTestScope, Task> body)
    {
        var f = new ImagePreparationTestScope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) release();
            f.Before = null; f.Failures.Clear();
            foreach (var (task, expected) in f.Tasks)
                try { await task; } catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await f.Service.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    internal static async Task<ImageData> Image(int width = 4, int height = 4, ImageFormat format = ImageFormat.Png, byte[]? pixels = null)
    {
        pixels ??= Enumerable.Range(0, width * height).SelectMany(_ => new byte[] { 0, 0, 0, 255 }).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var id = format switch { ImageFormat.Png => BitmapEncoder.PngEncoderId, ImageFormat.Jpeg => BitmapEncoder.JpegEncoderId, _ => BitmapEncoder.BmpEncoderId };
        var encoder = await BitmapEncoder.CreateAsync(id, stream).AsTask();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask();
        stream.Seek(0);
        var buffer = await stream.ReadAsync(new global::Windows.Storage.Streams.Buffer((uint)stream.Size), (uint)stream.Size, InputStreamOptions.None).AsTask();
        using var reader = DataReader.FromBuffer(buffer); var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes);
        return new(bytes, format, width, height);
    }
    internal static async Task<byte[]> Pixels(ImageData image)
    {
        await using var decoder = new WinRtImageDecoderIo();
        await decoder.WriteAsync(image.Bytes); var header = await decoder.OpenAsync();
        if (header.Format != image.Format || header.Width != image.PixelWidth || header.Height != image.PixelHeight) throw new InvalidOperationException("Output metadata mismatch.");
        return await decoder.PixelsAsync(image.PixelWidth, image.PixelHeight);
    }
    internal sealed class Decoder(ImagePreparationTestScope owner) : IImageDecoderIo, IAsyncDisposable
    {
        private readonly WinRtImageDecoderIo _inner = new();
        internal bool WriterDisposed, StreamDisposed;
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public Task WriteAsync(byte[] bytes) => owner.Call("D.Write", () => _inner.WriteAsync(bytes));
        public async Task<ImageDecoderHeader> OpenAsync()
        {
            ImageDecoderHeader result = default; await owner.Call("D.Open", async () => result = await _inner.OpenAsync()); return owner.Header ?? result;
        }
        public async Task<byte[]> PixelsAsync(int width, int height)
        {
            byte[] result = []; await owner.Call("D.Pixels", async () => result = await _inner.PixelsAsync(width, height)); return result;
        }
        public async ValueTask DisposeWriterAsync() { await owner.Call("D.Writer", async () => await _inner.DisposeWriterAsync()); WriterDisposed = true; }
        public async ValueTask DisposeStreamAsync() { await owner.Call("D.Stream", async () => await _inner.DisposeStreamAsync()); StreamDisposed = true; }
    }
    internal sealed class Encoder(ImagePreparationTestScope owner, int number) : IScreenEncoderIo, IAsyncDisposable
    {
        private readonly WinRtScreenEncoderIo _inner = new();
        internal bool ReaderDisposed, StreamDisposed;
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        internal float Quality;
        internal ImageFormat Format;
        internal byte[]? Source;
        public Task CreateAsync(ImageFormat format, float jpegQuality) { Format = format; Quality = jpegQuality; return owner.Call("E.Create", () => _inner.CreateAsync(format, jpegQuality)); }
        public void SetPixels(byte[] pixels, int width, int height) { Source = pixels; _inner.SetPixels(pixels, width, height); }
        public Task FlushAsync() => owner.Call("E.Flush", _inner.FlushAsync);
        public ulong Size => owner.EncodedSize?.Invoke(number) ?? _inner.Size;
        public async Task<uint> ReadAsync(uint count)
        {
            uint read = 0; await owner.Call("E.Read", async () => read = owner.EncodedSize != null ? count : await _inner.ReadAsync(count)); return owner.ReadLength ?? read;
        }
        public void CopyBytes(byte[] target) { if (owner.EncodedBytes != null) owner.EncodedBytes(number).CopyTo(target, 0); else if (owner.EncodedSize != null) target.AsSpan().Fill(47); else _inner.CopyBytes(target); }
        public async ValueTask DisposeReaderAsync() { await owner.Call("E.Reader", async () => await _inner.DisposeReaderAsync()); ReaderDisposed = true; }
        public async ValueTask DisposeStreamAsync() { await owner.Call("E.Stream", async () => await _inner.DisposeStreamAsync()); StreamDisposed = true; }
    }
}
