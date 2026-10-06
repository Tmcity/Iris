using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.Imaging;
using Windows.Graphics.Imaging;

namespace Iris.Platform.Windows.Ocr;

internal sealed class WindowsOcrProviderFactory(IWindowsOcrNative native, IWindowsOcrImageFactory images, TimeProvider time) : IOcrProviderFactory
{
    private readonly IWindowsOcrNative _native = native ?? throw new ArgumentNullException(nameof(native));
    private readonly IWindowsOcrImageFactory _images = images ?? throw new ArgumentNullException(nameof(images));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    public OcrProviderKind Kind => OcrProviderKind.WindowsLocal;
    public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(credentials);
        if (profile.Kind != Kind) throw new ArgumentException("Incorrect OCR provider kind.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.TimeoutSeconds is < 5 or > 120) throw new WindowsOcrException(ErrorCode.InvalidArgument, "PROFILE", profile.Id);
        uint capability;
        try { capability = _native.MaxImageDimension; }
        catch (Exception ex) { throw new WindowsOcrException(ErrorCode.ProviderUnavailable, "CAPABILITY", profile.Id, WindowsOcrErrors.Describe("CAPABILITY", ex)); }
        if (capability < 40) throw new WindowsOcrException(ErrorCode.ProviderUnavailable, "CAPABILITY", profile.Id);
        return new WindowsOcrProvider(profile, (int)Math.Min(capability, 32768), _native, _images, _time);
    }
}

internal sealed class WindowsOcrProvider : IOcrProvider, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1);
    private readonly IWindowsOcrNative _native;
    private readonly IWindowsOcrImageFactory _images;
    private readonly TimeProvider _time;
    private readonly string? _language;
    private readonly TimeSpan _budget;
    private readonly HashSet<WindowsOcrCall> _calls = [];
    private readonly List<ImageLease> _pendingImages = [];
    private readonly List<WindowsOcrCall> _pendingCalls = [];
    private Task? _disposal, _retry;
    private bool _stopping, _disposed;
    private int _waiters;
    internal int WaiterCount => Volatile.Read(ref _waiters);
    public ProviderDescriptor Descriptor { get; }
    public ImageConstraints Constraints { get; }
    internal WindowsOcrProvider(OcrProviderProfile profile, int maximum, IWindowsOcrNative native, IWindowsOcrImageFactory images, TimeProvider time)
    {
        _native = native; _images = images; _time = time; _language = profile.WindowsOptions?.LanguageTag; _budget = TimeSpan.FromSeconds(profile.TimeoutSeconds);
        Descriptor = new(profile.Id, string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName, nameof(OcrProviderKind.WindowsLocal), null, true);
        Constraints = new(ImagePreparationRules.MaxEncoded, false, maximum, 40, Array.AsReadOnly(new[] { ImageFormat.Png, ImageFormat.Bmp }));
    }
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) => Admit(async call =>
    {
        var image = request?.Image;
        if (image?.Bytes == null || image.Bytes.Length == 0 || image.Format is not (ImageFormat.Png or ImageFormat.Bmp) || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new WindowsOcrException(ErrorCode.ImageInvalid, "IMAGE", Descriptor.Id);
        ImagePreparationRules.PixelBytes(image.PixelWidth, image.PixelHeight);
        if (image.Bytes.LongLength > Constraints.MaxEncodedBytes || Math.Max(image.PixelWidth, image.PixelHeight) > Constraints.MaxLongSide || Math.Min(image.PixelWidth, image.PixelHeight) < 40)
            throw new WindowsOcrException(ErrorCode.ImageTooLarge, "IMAGE_BUDGET", Descriptor.Id);
        IWindowsOcrEngine? engine = Select(request!.LanguageHint, call);
        ImageLease? lease = null; SoftwareBitmap? bitmap = null;
        IReadOnlyList<OcrLine>? lines = null; Exception? failure = null; string stage = "IMAGE_CREATE";
        try
        {
            call.Check(); lease = new(_images.Create());
            stage = "WRITE"; call.Check(); await lease.Image.WriteAsync(image.Bytes).ConfigureAwait(false); call.Check();
            stage = "HEADER"; var header = await lease.Image.HeaderAsync().ConfigureAwait(false); call.Check();
            ImagePreparationRules.PixelBytes(header.Width, header.Height);
            if (header.Width != image.PixelWidth || header.Height != image.PixelHeight || header.Format != image.Format)
                throw new WindowsOcrException(ErrorCode.ImageInvalid, "HEADER_MISMATCH", Descriptor.Id);
            stage = "BITMAP"; bitmap = await lease.Image.BitmapAsync().ConfigureAwait(false); call.Check();
            if (bitmap.PixelWidth != image.PixelWidth || bitmap.PixelHeight != image.PixelHeight || bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                throw new WindowsOcrException(ErrorCode.ImageInvalid, "BITMAP_MISMATCH", Descriptor.Id);
            stage = "RECOGNIZE"; var nativeLines = await engine.RecognizeAsync(bitmap, call.Check).ConfigureAwait(false); call.Check();
            stage = "RESULT"; lines = WindowsOcrResults.Convert(nativeLines, call.Check);
        }
        catch (Exception ex) { failure = call.Classify(stage, ex); }
        finally { engine = null; bitmap = null; }
        if (lease != null)
        {
            var errors = await lease.CleanupAsync().ConfigureAwait(false);
            if (errors.Count != 0)
            {
                _pendingImages.Add(lease);
                throw new WindowsOcrException(ErrorCode.Unknown, "IMAGE_CLEANUP", Descriptor.Id,
                    $"primary=[{(failure == null ? "none" : WindowsOcrErrors.Describe("PRIMARY", failure))}];cleanup=[{string.Join(';', errors)}]");
            }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        call.Check(); return new OcrResult(Descriptor.Id, lines!, null, TimeSpan.Zero);
    }, (value, elapsed) => value with { Elapsed = elapsed }, null, cancellationToken);

    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) => Admit(call =>
    {
        _ = Select(null, call); call.Check();
        return Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero));
    }, (value, elapsed) => value with { Latency = elapsed },
        (error, elapsed) => new ProviderTestResult(false, elapsed, Error: error.ToErrorInfo()), cancellationToken);

    private IWindowsOcrEngine Select(string? hint, WindowsOcrCall call)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? mapped = hint?.ToLowerInvariant() switch
        {
            "zh-hans" => "zh-Hans-CN", "zh-hant" => "zh-Hant-TW", "en" => "en-US", "ja" => "ja-JP", "ko" => "ko-KR",
            "auto" => null, _ => hint
        };
        foreach (var tag in new[] { _language, mapped })
        {
            if (string.IsNullOrWhiteSpace(tag) || !tried.Add(tag)) continue;
            var engine = SelectNative(() => _native.TryLanguage(tag), call); if (engine != null) return engine;
        }
        var user = SelectNative(_native.TryUserLanguages, call);
        return user ?? throw new WindowsOcrException(ErrorCode.OcrLanguageUnsupported, "LANGUAGE", Descriptor.Id);
    }
    private static IWindowsOcrEngine? SelectNative(Func<IWindowsOcrEngine?> select, WindowsOcrCall call)
    {
        call.Check();
        try { var engine = select(); call.Check(); return engine; }
        catch (Exception ex) { throw call.Classify("LANGUAGE", ex); }
    }
    private Task<T> Admit<T>(Func<WindowsOcrCall, Task<T>> body, Func<T, TimeSpan, T> stamp,
        Func<WindowsOcrException, TimeSpan, T>? failed, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping) throw new WindowsOcrException(ErrorCode.Unknown, "STOPPED", Descriptor.Id);
            var call = new WindowsOcrCall(_time, _budget, Descriptor.Id, caller);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); _calls.Add(call);
            _ = Task.Run(async () =>
            {
                T? result = default; Exception? failure = null; bool entered = false;
                try
                {
                    call.Start(); call.Check(); Interlocked.Increment(ref _waiters);
                    try { await _serial.WaitAsync(call.Token).ConfigureAwait(false); entered = true; }
                    finally { Interlocked.Decrement(ref _waiters); }
                    call.Check(); await CleanupPendingAsync().ConfigureAwait(false); call.Check();
                    result = await body(call).ConfigureAwait(false);
                }
                catch (Exception ex) { failure = call.Classify("CALL", ex); }
                finally { if (entered) _serial.Release(); }
                try { await call.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (call.Pending) { lock (_gate) _pendingCalls.Add(call); }
                    failure = new WindowsOcrException(ErrorCode.Unknown, "CALL_CLEANUP", Descriptor.Id,
                        $"primary=[{(failure == null ? "none" : WindowsOcrErrors.Describe("PRIMARY", failure))}];{WindowsOcrErrors.Describe("CLEANUP", ex)}");
                }
                lock (_gate)
                {
                    if (failure == null) { try { call.Check(); } catch (Exception ex) { failure = ex; } }
                    if (failure is OperationCanceledException) completion.TrySetCanceled(caller.IsCancellationRequested ? caller : new CancellationToken(true));
                    else if (failure is WindowsOcrException error && failed != null) completion.TrySetResult(failed(error, _time.GetElapsedTime(call.Started)));
                    else if (failure != null) completion.TrySetException(failure);
                    else completion.TrySetResult(stamp(result!, _time.GetElapsedTime(call.Started)));
                    _calls.Remove(call); call.Done.TrySetResult();
                }
            }, CancellationToken.None);
            return completion.Task;
        }
    }
    private async Task CleanupPendingAsync()
    {
        var errors = new List<string>();
        foreach (var lease in _pendingImages.ToArray())
        {
            var current = await lease.CleanupAsync().ConfigureAwait(false);
            if (current.Count == 0) _pendingImages.Remove(lease); else errors.AddRange(current);
        }
        WindowsOcrCall[] calls; lock (_gate) calls = _pendingCalls.ToArray();
        foreach (var call in calls)
            try { await call.DisposeAsync().ConfigureAwait(false); lock (_gate) _pendingCalls.Remove(call); }
            catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("CALL", ex)); }
        if (errors.Count != 0) throw new WindowsOcrException(ErrorCode.Unknown, "RETRY", Descriptor.Id, string.Join(';', errors));
    }
    internal Task RetryCleanupAsync()
    {
        lock (_gate)
        {
            if (_disposal is { IsCompleted: false }) return _disposal;
            if (_disposed) return Task.CompletedTask;
            if (_retry is { IsCompleted: false }) return _retry;
            return _retry = Task.Run(async () =>
            {
                await _serial.WaitAsync().ConfigureAwait(false);
                try { await CleanupPendingAsync().ConfigureAwait(false); } finally { _serial.Release(); }
            });
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_disposed) return ValueTask.CompletedTask;
            if (_disposal is { IsCompleted: false }) return new(_disposal);
            var calls = _calls.ToArray(); foreach (var call in calls) call.Cancel(true); var retry = _retry;
            return new(_disposal = Task.Run(async () =>
            {
                await Task.WhenAll(calls.Select(c => c.Done.Task)).ConfigureAwait(false);
                if (retry != null) { try { await retry.ConfigureAwait(false); } catch (WindowsOcrException) { /* Retry the original responsibilities below. */ } }
                await _serial.WaitAsync().ConfigureAwait(false);
                try { await CleanupPendingAsync().ConfigureAwait(false); } finally { _serial.Release(); }
                lock (_gate) { _disposed = true; _serial.Dispose(); }
            }));
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_calls.Count != 0 || _pendingImages.Count != 0 || _pendingCalls.Count != 0 || _retry is { IsCompleted: false } || _disposal is { IsCompleted: false })
                throw new WindowsOcrException(ErrorCode.Unknown, "ASYNC_DISPOSE_REQUIRED", Descriptor.Id);
            _stopping = _disposed = true; _serial.Dispose();
        }
    }
    private sealed class ImageLease(IWindowsOcrImage image)
    {
        internal IWindowsOcrImage Image { get; } = image;
        private bool _bitmap, _writer, _stream;
        internal async Task<List<string>> CleanupAsync()
        {
            var errors = new List<string>();
            if (!_bitmap) try { await Image.DisposeBitmapAsync().ConfigureAwait(false); _bitmap = true; } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("BITMAP", ex)); }
            if (!_writer) try { await Image.DisposeWriterAsync().ConfigureAwait(false); _writer = true; } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("WRITER", ex)); }
            if (!_stream) try { await Image.DisposeStreamAsync().ConfigureAwait(false); _stream = true; } catch (Exception ex) { errors.Add(WindowsOcrErrors.Describe("STREAM", ex)); }
            return errors;
        }
    }
}
