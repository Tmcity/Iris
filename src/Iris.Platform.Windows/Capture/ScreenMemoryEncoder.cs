using Iris.Core.Errors;
using Iris.Core.Imaging;

namespace Iris.Platform.Windows.Capture;

internal sealed class ScreenMemoryEncoder(IScreenEncoderFactory factory)
{
    private Attempt? _pending;
    internal async Task<ImageData> EncodeAsync(byte[] pixels, int width, int height, ImageFormat format, Action check, Func<bool> cancelled)
    {
        Exception? body = null;
        string stage = "ENCODER_FACTORY";
        ImageData? image = null;
        try
        {
            check();
            _pending = new(factory.Create(), pixels);
            stage = "ENCODER_CREATE";
            await _pending.Io.CreateAsync(format, 0.9f).ConfigureAwait(false); check();
            stage = "ENCODER_PIXELS";
            _pending.Io.SetPixels(pixels, width, height); check();
            stage = "ENCODER_FLUSH";
            await _pending.Io.FlushAsync().ConfigureAwait(false); check();
            stage = "ENCODER_SIZE";
            ulong size = _pending.Io.Size;
            int length = ScreenPixelLayout.EncodedLength(size);
            check();
            var bytes = new byte[length];
            stage = "ENCODER_READ";
            uint read = await _pending.Io.ReadAsync((uint)size).ConfigureAwait(false); check();
            if (read != size) throw ScreenCaptureErrors.Fail("ENCODER_SHORT_READ");
            _pending.Io.CopyBytes(bytes); check();
            image = new(bytes, format, width, height);
        }
        catch (Exception ex) { body = ex; }
        var cleanup = await RetryCleanupAsync().ConfigureAwait(false);
        if (cleanup.Count != 0 || body != null && (body is not OperationCanceledException || !cancelled()))
            throw ScreenCaptureErrors.Combine(stage, body, cleanup, cancelled());
        if (body is OperationCanceledException) throw body;
        check(); return image!;
    }
    internal async Task<IReadOnlyList<string>> RetryCleanupAsync()
    {
        var errors = new List<string>();
        if (_pending is not { } pending) return errors;
        if (!pending.ReaderReleased)
        {
            try { await pending.Io.DisposeReaderAsync().ConfigureAwait(false); pending.ReaderReleased = true; }
            catch (Exception ex) { errors.Add(ScreenCaptureErrors.Describe("ENCODER_READER_DISPOSE", ex)); }
        }
        if (!pending.StreamReleased)
        {
            try { await pending.Io.DisposeStreamAsync().ConfigureAwait(false); pending.StreamReleased = true; }
            catch (Exception ex) { errors.Add(ScreenCaptureErrors.Describe("ENCODER_STREAM_DISPOSE", ex)); }
        }
        if (pending.ReaderReleased && pending.StreamReleased) _pending = null;
        return errors;
    }
    private sealed class Attempt(IScreenEncoderIo io, byte[] pixels)
    {
        internal readonly IScreenEncoderIo Io = io;
        // Keep the input alive until all actual codec work and owned resources have ended.
        internal readonly byte[] Pixels = pixels;
        internal bool ReaderReleased, StreamReleased;
    }
}
