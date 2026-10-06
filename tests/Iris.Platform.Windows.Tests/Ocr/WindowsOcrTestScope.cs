using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.Imaging;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.Time.Testing;
using Windows.Graphics.Imaging;

namespace Iris.Platform.Windows.Tests.Ocr;

internal sealed class WindowsOcrTestScope
{
    internal OcrProviderProfile Profile { get; set; } = new() { Id = "local-test", DisplayName = "Local test", Kind = OcrProviderKind.WindowsLocal };
    internal readonly Clock Time = new();
    internal NativeBoundary Native { get; }
    internal ImageFactory Images { get; }
    private WindowsOcrProvider? _provider;
    internal WindowsOcrProvider Provider => _provider ??= (WindowsOcrProvider)new WindowsOcrProviderFactory(Native, Images, Time).Create(Profile, new("unused-key", "unused-secret"));
    internal readonly List<(Task Task, Type? Expected)> Tasks = [];
    internal readonly List<Action> Releases = [];
    internal readonly Dictionary<string, Exception> Failures = [];
    internal Func<string, Task>? Before { get; set; }
    internal Action<string>? BeforeLanguage { get; set; }
    internal ImageDecoderHeader? Header { get; set; }
    internal IReadOnlyList<WindowsOcrNativeLine> Result { get; set; } = [new("你 好", [new(1, 2, 10, 12)])];
    internal int InFlight;
    internal WindowsOcrTestScope() { Native = new(this); Images = new(this); }
    internal T Track<T>(T task, Type? expected = null) where T : Task { Tasks.Add((task, expected)); return task; }
    internal Task<OcrResult> Recognize(ImageData image, string hint = "en", Type? expected = null, CancellationToken token = default) =>
        Track(Provider.RecognizeAsync(new(image, hint), token), expected);
    internal async Task Call(string stage, Func<Task> work)
    {
        if (stage.EndsWith("DISPOSE", StringComparison.Ordinal) && InFlight != 0) throw new InvalidOperationException("Early bitmap/image disposal.");
        InFlight++;
        try
        {
            if (Before != null) await Before(stage);
            if (Failures.TryGetValue(stage, out var failure)) throw failure;
            await work();
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
    internal static Task<ImageData> Image(int width = 40, int height = 40, ImageFormat format = ImageFormat.Png) =>
        Imaging.ImagePreparationTestScope.Image(width, height, format);
    internal static async Task Run(Func<WindowsOcrTestScope, Task> body)
    {
        var f = new WindowsOcrTestScope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) release();
            f.Before = null; f.BeforeLanguage = null; f.Failures.Clear(); f.Time.FailDispose = false; f.Time.AfterTimestampRead = null;
            Task stop = f._provider?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            foreach (var (task, expected) in f.Tasks)
                try { await task; } catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await stop; } catch (Exception ex) { errors.Add(ex); }
            if (f.Images.Items.Any(i => !i.BitmapDisposed || !i.WriterDisposed || !i.StreamDisposed) || f.InFlight != 0)
                errors.Add(new InvalidOperationException("Owned image did not drain."));
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    internal sealed class NativeBoundary(WindowsOcrTestScope owner) : IWindowsOcrNative
    {
        internal uint Capability { get; set; } = 4096;
        internal int CapabilityCalls, UserCalls, RecognitionCalls;
        internal readonly List<string> Attempts = [];
        internal readonly List<Engine> Engines = [];
        internal readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
        { ["en-US"] = "en", ["zh-Hans-CN"] = "zh-Hans", ["ja-JP"] = "ja", ["ko-KR"] = "ko", ["zh-Hant-TW"] = "zh-Hant", ["fr-FR"] = "fr" };
        internal string? UserLanguage { get; set; } = "en";
        public uint MaxImageDimension { get { CapabilityCalls++; if (owner.Failures.TryGetValue("CAPABILITY", out var error)) throw error; return Capability; } }
        public IWindowsOcrEngine? TryLanguage(string tag)
        {
            Attempts.Add(tag); owner.BeforeLanguage?.Invoke(tag);
            if (owner.Failures.TryGetValue("LANGUAGE", out var error)) throw error;
            if (!Languages.TryGetValue(tag, out string? actual)) return null;
            var engine = new Engine(owner, actual); Engines.Add(engine); return engine;
        }
        public IWindowsOcrEngine? TryUserLanguages()
        {
            UserCalls++; owner.BeforeLanguage?.Invoke("USER");
            if (owner.Failures.TryGetValue("USER", out var error)) throw error;
            if (UserLanguage == null) return null;
            var engine = new Engine(owner, UserLanguage); Engines.Add(engine); return engine;
        }
    }
    internal sealed class Engine(WindowsOcrTestScope owner, string language) : IWindowsOcrEngine
    {
        public string LanguageTag => language;
        internal SoftwareBitmap? SeenBitmap;
        public async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(SoftwareBitmap bitmap, Action check)
        {
            SeenBitmap = bitmap; owner.Native.RecognitionCalls++;
            await owner.Call("RECOGNIZE", () => Task.CompletedTask);
            return owner.Result;
        }
    }
    internal sealed class ImageFactory(WindowsOcrTestScope owner) : IWindowsOcrImageFactory
    {
        internal readonly List<ImageSession> Items = [];
        public IWindowsOcrImage Create() { var item = new ImageSession(owner); Items.Add(item); return item; }
    }
    internal sealed class ImageSession(WindowsOcrTestScope owner) : IWindowsOcrImage, IAsyncDisposable
    {
        private readonly WindowsOcrImage _actual = new();
        internal bool BitmapDisposed, WriterDisposed, StreamDisposed;
        internal SoftwareBitmap? Bitmap;
        public Task WriteAsync(byte[] bytes) => owner.Call("WRITE", () => _actual.WriteAsync(bytes));
        public async Task<ImageDecoderHeader> HeaderAsync()
        {
            ImageDecoderHeader value = default; await owner.Call("HEADER", async () => value = await _actual.HeaderAsync()); return owner.Header ?? value;
        }
        public async Task<SoftwareBitmap> BitmapAsync()
        { await owner.Call("BITMAP", async () => Bitmap = await _actual.BitmapAsync()); return Bitmap!; }
        public async ValueTask DisposeBitmapAsync() { await owner.Call("BITMAP_DISPOSE", async () => await _actual.DisposeBitmapAsync()); BitmapDisposed = true; }
        public async ValueTask DisposeWriterAsync() { await owner.Call("WRITER_DISPOSE", async () => await _actual.DisposeWriterAsync()); WriterDisposed = true; }
        public async ValueTask DisposeStreamAsync() { await owner.Call("STREAM_DISPOSE", async () => await _actual.DisposeStreamAsync()); StreamDisposed = true; }
        public ValueTask DisposeAsync() => _actual.DisposeAsync();
    }
    internal sealed class Clock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource> _timers = new();
        internal Action? AfterTimestampRead { get; set; }
        internal bool FailDispose { get; set; }
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() { long stamp = _inner.GetTimestamp(); AfterTimestampRead?.Invoke(); return stamp; }
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            _timers.GetOrAdd(dueTime.Ticks, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return new Timer(this, timer);
        }
        internal void Advance(TimeSpan time) => _inner.Advance(time);
        internal Task TimerCreated(TimeSpan due) => _timers.GetOrAdd(due.Ticks, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        private sealed class Timer(Clock owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose() { if (owner.FailDispose) throw new IOException("private timer"); inner.Dispose(); }
            public async ValueTask DisposeAsync() { if (owner.FailDispose) throw new IOException("private timer"); await inner.DisposeAsync(); }
        }
    }
}
