using Iris.Core.Errors;
using Iris.Core.Imaging;
using static Iris.Platform.Windows.Imaging.ImagePreparationRules;

namespace Iris.Platform.Windows.Imaging;

internal sealed class WinRtImagePreprocessor(IImagePreparationFactory factory) : IImagePreprocessor, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1);
    private readonly HashSet<Task> _work = [];
    private readonly List<Resource> _retained = [];
    private volatile bool _stopping;
    private bool _disposed;
    private Task? _stop;

    public Task<ImageData> PrepareAsync(ImageData image, ImageConstraints constraints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageConstraints snapshot;
        try { snapshot = Snapshot(constraints); }
        catch (ImagePreparationException) { throw; }
        catch (Exception ex) { throw new ImagePreparationException(ErrorCode.InvalidArgument, Describe("CONSTRAINT_SNAPSHOT", ex)); }
        Validate(image);
        var layout = Layout(image.PixelWidth, image.PixelHeight, snapshot);
        lock (_gate)
        {
            if (_stopping || _disposed) throw Fail("STOPPED");
            return Schedule(async () =>
            {
                void Check() { cancellationToken.ThrowIfCancellationRequested(); if (_stopping) throw new OperationCanceledException(); }
                Check();
                await CleanupPendingAsync().ConfigureAwait(false);
                return await PrepareCoreAsync(image, snapshot, layout, Check,
                    () => cancellationToken.IsCancellationRequested || _stopping).ConfigureAwait(false);
            }, cancellationToken);
        }
    }

    // The service holds only non-result barriers; completed ImageData tasks never become a cache.
    private Task<T> Schedule<T>(Func<Task<T>> operation, CancellationToken token, bool cancelOnStop = true)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(barrier.Task);
        _ = Task.Run(async () =>
        {
            bool entered = false;
            T? result = default;
            Exception? failure = null;
            try
            {
                await _serial.WaitAsync(token).ConfigureAwait(false); entered = true;
                result = await operation().ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; }
            finally { if (entered) _serial.Release(); }
            lock (_gate)
            {
                bool canceled = token.IsCancellationRequested || (cancelOnStop && _stopping);
                if ((failure == null || failure is OperationCanceledException) && canceled)
                    completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
                else if (failure != null)
                    completion.TrySetException(failure is ImagePreparationException ? failure : new ImagePreparationException(ErrorCode.Unknown, Describe("WORK", failure)));
                else completion.TrySetResult(result!);
                _work.Remove(barrier.Task); barrier.TrySetResult();
            }
        });
        return completion.Task;
    }

    private async Task<ImageData> PrepareCoreAsync(ImageData image, ImageConstraints c, ImagePreparationLayout layout, Action check, Func<bool> canceled)
    {
        bool FitsBudgets(byte[] bytes) => Fits(bytes.LongLength, c) &&
            (c.MaxFormUrlEncodedBytes is not { } limit || FormUrlEncodedImageLength.Count(bytes, () =>
            {
                check(); factory.CheckFormBudgetBatch(); check();
            }) <= limit);
        bool unchanged = layout.Width == image.PixelWidth && layout.Height == image.PixelHeight &&
            layout.CanvasWidth == image.PixelWidth && layout.CanvasHeight == image.PixelHeight;
        bool accepted = c.AcceptedFormats.Contains(image.Format);
        bool fast = unchanged && accepted && FitsBudgets(image.Bytes);
        check();
        var decoder = factory.CreateDecoder(); // Factories create managed owners; I/O starts only after registration below.
        var decoded = await UseAsync(new Resource(("DECODER_WRITER", decoder.DisposeWriterAsync), ("DECODER_STREAM", decoder.DisposeStreamAsync)), async () =>
        {
            await CallAsync("WRITE", () => decoder.WriteAsync(image.Bytes), ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            var header = await CallAsync("DECODE", decoder.OpenAsync, ErrorCode.ImageInvalid, check, canceled).ConfigureAwait(false);
            PixelBytes(header.Width, header.Height);
            if (header.Format != image.Format || header.Width != image.PixelWidth || header.Height != image.PixelHeight)
                throw Fail("HEADER_MISMATCH", ErrorCode.ImageInvalid);
            var pixels = await CallAsync("PIXELS", () => decoder.PixelsAsync(layout.Width, layout.Height), ErrorCode.ImageInvalid, check, canceled).ConfigureAwait(false);
            if (pixels.Length != PixelBytes(layout.Width, layout.Height)) throw Fail("PIXEL_LENGTH", ErrorCode.ImageInvalid);
            return pixels;
        }, canceled).ConfigureAwait(false);
        check();
        if (fast) return image;
        byte[] canvas = WhiteCanvas(decoded, layout, check);
        decoded = null!;
        ImageFormat format = accepted ? image.Format : c.AcceptedFormats[0];
        int nextQuality = 90;
        // If only the original bytes exceed the budget, do not try the original format again.
        if (!unchanged || !accepted)
        {
            var output = await EncodeAsync(canvas, layout, format, 90, check, canceled).ConfigureAwait(false);
            if (FitsBudgets(output.Bytes)) return output;
            if (format == ImageFormat.Jpeg) nextQuality = 80;
        }
        if (!c.AcceptedFormats.Contains(ImageFormat.Jpeg)) throw Fail("BYTE_BUDGET", ErrorCode.ImageTooLarge);
        for (int quality = nextQuality; quality >= 50; quality -= 10)
        {
            check();
            var output = await EncodeAsync(canvas, layout, ImageFormat.Jpeg, quality, check, canceled).ConfigureAwait(false);
            if (FitsBudgets(output.Bytes)) return output;
        }
        throw Fail("JPEG_BUDGET", ErrorCode.ImageTooLarge);
    }

    private async Task<ImageData> EncodeAsync(byte[] pixels, ImagePreparationLayout layout, ImageFormat format, int quality, Action check, Func<bool> canceled)
    {
        check();
        var encoder = factory.CreateEncoder();
        return await UseAsync(new Resource(("ENCODER_READER", encoder.DisposeReaderAsync), ("ENCODER_STREAM", encoder.DisposeStreamAsync)), async () =>
        {
            await CallAsync("ENCODER_CREATE", () => encoder.CreateAsync(format, quality / 100f), ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            await CallAsync("SET_PIXELS", () => { encoder.SetPixels(pixels, layout.CanvasWidth, layout.CanvasHeight); return Task.CompletedTask; }, ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            await CallAsync("FLUSH", encoder.FlushAsync, ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            ulong size = await CallAsync("SIZE", () => Task.FromResult(encoder.Size), ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            if (size > MaxEncoded) throw Fail("ENCODED_BUDGET", ErrorCode.ImageTooLarge);
            if (size == 0) throw Fail("EMPTY_ENCODING");
            uint read = await CallAsync("READ", () => encoder.ReadAsync((uint)size), ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            if (read != size) throw Fail("SHORT_READ");
            var bytes = new byte[(int)size];
            await CallAsync("COPY", () => { encoder.CopyBytes(bytes); return Task.CompletedTask; }, ErrorCode.Unknown, check, canceled).ConfigureAwait(false);
            return new ImageData(bytes, format, layout.CanvasWidth, layout.CanvasHeight);
        }, canceled).ConfigureAwait(false);
    }

    private static async Task<T> CallAsync<T>(string stage, Func<Task<T>> call, ErrorCode code, Action check, Func<bool> canceled)
    {
        check();
        T result;
        try { result = await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (canceled()) { throw; }
        catch (Exception ex)
        {
            // Memory decoder data errors are distinct from failures of the surrounding I/O boundary.
            bool invalidData = ex.HResult is unchecked((int)0x88982F07) or unchecked((int)0x88982F50) or
                unchecked((int)0x88982F60) or unchecked((int)0x88982F61) or unchecked((int)0x88982F62) or
                unchecked((int)0x88982F63) or unchecked((int)0x88982F70) or unchecked((int)0x88982F72) or unchecked((int)0x80070057);
            throw new ImagePreparationException(code == ErrorCode.ImageInvalid && invalidData ? ErrorCode.ImageInvalid : ErrorCode.Unknown, Describe(stage, ex));
        }
        check(); return result;
    }
    private static Task<bool> CallAsync(string stage, Func<Task> call, ErrorCode code, Action check, Func<bool> canceled) =>
        CallAsync(stage, async () => { await call().ConfigureAwait(false); return true; }, code, check, canceled);

    private async Task<T> UseAsync<T>(Resource resource, Func<Task<T>> body, Func<bool> canceled)
    {
        T? result = default; Exception? failure = null;
        try { result = await body().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        var errors = await resource.CleanupAsync().ConfigureAwait(false);
        if (errors.Count != 0)
        {
            _retained.Add(resource);
            throw new ImagePreparationException(ErrorCode.Unknown,
                $"primary=[{(failure == null ? "none" : Describe("PRIMARY", failure))}];cancelled={canceled()};cleanup=[{string.Join(';', errors)}]");
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
    private async Task CleanupPendingAsync()
    {
        var errors = new List<string>();
        foreach (var resource in _retained.ToArray())
        {
            var current = await resource.CleanupAsync().ConfigureAwait(false);
            if (current.Count == 0) _retained.Remove(resource); else errors.AddRange(current);
        }
        if (errors.Count != 0) throw new ImagePreparationException(ErrorCode.Unknown, $"stage=RETRY;cleanup=[{string.Join(';', errors)}]");
    }
    internal Task RetryCleanupAsync()
    {
        lock (_gate)
        {
            if (_stop is { IsCompleted: false }) return _stop;
            if (_disposed || _stop is { IsCompletedSuccessfully: true }) return Task.CompletedTask;
            return Schedule(async () => { await CleanupPendingAsync().ConfigureAwait(false); return true; }, CancellationToken.None, cancelOnStop: false);
        }
    }
    internal void StopAcceptance() { lock (_gate) _stopping = true; }
    internal Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_disposed || _stop is { IsCompletedSuccessfully: true }) return Task.CompletedTask;
            if (_stop is { IsCompleted: false }) return _stop;
            var barriers = _work.ToArray();
            return _stop = Task.Run(async () =>
            {
                await Task.WhenAll(barriers).ConfigureAwait(false);
                await _serial.WaitAsync().ConfigureAwait(false);
                try { await CleanupPendingAsync().ConfigureAwait(false); }
                finally { _serial.Release(); }
            });
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); Dispose(); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_work.Count != 0 || _retained.Count != 0 || _stop is { IsCompleted: false }) throw Fail("ASYNC_SHUTDOWN_REQUIRED");
            _disposed = _stopping = true; _serial.Dispose();
        }
    }
    private sealed class Resource(params (string Stage, Func<ValueTask> Dispose)[] steps)
    {
        private readonly bool[] _done = new bool[steps.Length];
        internal async Task<List<string>> CleanupAsync()
        {
            var errors = new List<string>();
            for (int i = 0; i < steps.Length; i++)
            {
                if (_done[i]) continue;
                try { await steps[i].Dispose().ConfigureAwait(false); _done[i] = true; }
                catch (Exception ex) { errors.Add(Describe(steps[i].Stage, ex)); }
            }
            return errors;
        }
    }
}
