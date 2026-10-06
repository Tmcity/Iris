using System.Collections.Concurrent;
using Iris.Platform.Windows.Imaging;
using Iris.Platform.Windows.Ocr;
using Windows.Graphics.Imaging;

internal sealed class OcrBoundary : IWindowsOcrNative, IWindowsOcrImageFactory
{
    internal uint Maximum { get; set; } = 10000;
    internal int CapabilityReads;
    internal Exception? CapabilityFailure { get; set; }
    internal readonly ConcurrentQueue<string> LanguageRequests = new();
    internal readonly ConcurrentQueue<ImageOwner> Images = new();
    internal readonly ConcurrentQueue<WeakReference> Engines = new();
    internal readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { "en-US", "zh-Hans-CN" };
    internal string? UserLanguage { get; set; } = "en-US";
    internal Func<string, Task>? Before { get; set; }
    internal Func<SoftwareBitmap, Task>? BeforeRecognition { get; set; }
    internal Func<string, Exception?>? Failure { get; set; }
    internal IReadOnlyList<WindowsOcrNativeLine> Lines { get; set; } = [new("你 好", [new(1.25,2.5,10.5,6.25), new(14.75,3.1,4.4,5.2)])];
    internal int Recognitions, ActiveRecognitions, MaximumConcurrency;
    public uint MaxImageDimension
    {
        get { Interlocked.Increment(ref CapabilityReads); if (CapabilityFailure != null) throw CapabilityFailure; return Maximum; }
    }
    public IWindowsOcrEngine? TryLanguage(string tag)
    {
        LanguageRequests.Enqueue(tag);
        if (Failure?.Invoke("language") is { } failure) throw failure;
        return Supported.Contains(tag) ? CreateEngine(tag) : null;
    }
    public IWindowsOcrEngine? TryUserLanguages()
    {
        LanguageRequests.Enqueue("<user>");
        if (Failure?.Invoke("user-language") is { } failure) throw failure;
        return UserLanguage == null ? null : CreateEngine(UserLanguage);
    }
    private IWindowsOcrEngine CreateEngine(string tag)
    {
        var engine = new Engine(this, tag); Engines.Enqueue(new(engine)); return engine;
    }
    public IWindowsOcrImage Create()
    {
        var image = new ImageOwner(this); Images.Enqueue(image); return image;
    }
    private sealed class Engine(OcrBoundary owner, string tag) : IWindowsOcrEngine
    {
        public string LanguageTag => tag;
        public async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(SoftwareBitmap bitmap, Action check)
        {
            Interlocked.Increment(ref owner.Recognitions);
            int active = Interlocked.Increment(ref owner.ActiveRecognitions);
            owner.MaximumConcurrency = Math.Max(owner.MaximumConcurrency, active);
            try
            {
                if (owner.BeforeRecognition != null) await owner.BeforeRecognition(bitmap);
                if (owner.Before != null) await owner.Before("recognize");
                if (owner.Failure?.Invoke("recognize") is { } failure) throw failure;
                check(); return owner.Lines;
            }
            finally { Interlocked.Decrement(ref owner.ActiveRecognitions); }
        }
    }
    internal sealed class ImageOwner(OcrBoundary owner) : IWindowsOcrImage, IAsyncDisposable
    {
        private readonly WindowsOcrImage _actual = new();
        internal bool BitmapReleased, WriterReleased, StreamReleased;
        internal int BitmapReleaseCalls, WriterReleaseCalls, StreamReleaseCalls;
        internal WeakReference? BitmapReference;
        private async Task Before(string stage)
        {
            if (owner.Before != null) await owner.Before(stage);
            if (owner.Failure?.Invoke(stage) is { } failure) throw failure;
        }
        public async Task WriteAsync(byte[] bytes) { await Before("write"); await _actual.WriteAsync(bytes); }
        public async Task<ImageDecoderHeader> HeaderAsync() { await Before("header"); return await _actual.HeaderAsync(); }
        public async Task<SoftwareBitmap> BitmapAsync()
        {
            await Before("bitmap"); var bitmap = await _actual.BitmapAsync(); BitmapReference = new(bitmap); return bitmap;
        }
        public async ValueTask DisposeBitmapAsync()
        {
            BitmapReleaseCalls++; await Before("bitmap-release");
            if (owner.ActiveRecognitions != 0) throw new InvalidOperationException("Bitmap released before actual recognition ended.");
            await _actual.DisposeBitmapAsync(); BitmapReleased = true;
        }
        public async ValueTask DisposeWriterAsync() { WriterReleaseCalls++; await Before("writer-release"); await _actual.DisposeWriterAsync(); WriterReleased = true; }
        public async ValueTask DisposeStreamAsync() { StreamReleaseCalls++; await Before("stream-release"); await _actual.DisposeStreamAsync(); StreamReleased = true; }
        public ValueTask DisposeAsync() => _actual.DisposeAsync(); // Harness-only final fallback after product observations.
    }
}
