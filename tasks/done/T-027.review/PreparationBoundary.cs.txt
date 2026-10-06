using System.Collections.Concurrent;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Capture;
using Iris.Platform.Windows.Imaging;

// Only external codec/resource behavior is replaced; all selection, sequencing,
// cancellation, retention and cleanup decisions remain in the actual service.
internal sealed class PreparationBoundary : IImagePreparationFactory
{
    internal readonly ConcurrentQueue<Decoder> Decoders = new();
    internal readonly ConcurrentQueue<Encoder> Encoders = new();
    internal Func<string, Task>? Before;
    internal Action<string>? BeforeDispose;
    internal Func<ImageFormat, float, ulong> EncodedSize = (_, _) => 32;
    internal ImageDecoderHeader Header = new(ImageFormat.Png, 4, 4);
    internal ImageData Input => new(new byte[200], Header.Format, (int)Header.Width, (int)Header.Height);
    internal Task Step(string stage) => Before?.Invoke(stage) ?? Task.CompletedTask;
    public IImageDecoderIo CreateDecoder()
    {
        var decoder = new Decoder(this); Decoders.Enqueue(decoder); return decoder;
    }
    public IScreenEncoderIo CreateEncoder()
    {
        var encoder = new Encoder(this); Encoders.Enqueue(encoder); return encoder;
    }
    internal sealed class Decoder(PreparationBoundary owner) : IImageDecoderIo
    {
        internal bool WriterDisposed, StreamDisposed;
        internal int WriterDisposeCalls, StreamDisposeCalls;
        public Task WriteAsync(byte[] bytes) => owner.Step("write");
        public async Task<ImageDecoderHeader> OpenAsync() { await owner.Step("open"); return owner.Header; }
        public async Task<byte[]> PixelsAsync(int width, int height)
        {
            await owner.Step("pixels");
            var pixels = new byte[checked(width * height * 4)];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 17; pixels[i + 1] = 53; pixels[i + 2] = 221; pixels[i + 3] = 255; }
            return pixels;
        }
        public ValueTask DisposeWriterAsync()
        {
            WriterDisposeCalls++; owner.BeforeDispose?.Invoke("decoder-writer"); WriterDisposed = true; return ValueTask.CompletedTask;
        }
        public ValueTask DisposeStreamAsync()
        {
            StreamDisposeCalls++; owner.BeforeDispose?.Invoke("decoder-stream"); StreamDisposed = true; return ValueTask.CompletedTask;
        }
    }
    internal sealed class Encoder(PreparationBoundary owner) : IScreenEncoderIo
    {
        internal ImageFormat Format;
        internal float Quality;
        internal byte[]? PixelReference, PixelSnapshot;
        internal bool ReaderDisposed, StreamDisposed;
        internal int ReaderDisposeCalls, StreamDisposeCalls;
        public async Task CreateAsync(ImageFormat format, float jpegQuality)
        {
            Format = format; Quality = jpegQuality; await owner.Step("create");
        }
        public void SetPixels(byte[] pixels, int width, int height)
        {
            PixelReference = pixels; PixelSnapshot = pixels.ToArray();
        }
        public Task FlushAsync() => owner.Step("flush");
        public ulong Size => owner.EncodedSize(Format, Quality);
        public async Task<uint> ReadAsync(uint count) { await owner.Step("read"); return count; }
        public void CopyBytes(byte[] target) => target.AsSpan().Fill(0x3a);
        public ValueTask DisposeReaderAsync()
        {
            ReaderDisposeCalls++; owner.BeforeDispose?.Invoke("encoder-reader"); ReaderDisposed = true; return ValueTask.CompletedTask;
        }
        public ValueTask DisposeStreamAsync()
        {
            StreamDisposeCalls++; owner.BeforeDispose?.Invoke("encoder-stream"); StreamDisposed = true; return ValueTask.CompletedTask;
        }
    }
}
