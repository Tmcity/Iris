using System.Runtime.ExceptionServices;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.Time.Testing;

internal sealed class ProviderScope
{
    internal readonly OcrBoundary Boundary = new();
    internal readonly FakeTimeProvider Time = new(new DateTimeOffset(2026,10,6,0,0,0,TimeSpan.Zero));
    internal readonly List<Action> Releases = [];
    internal readonly IOcrProvider Provider;
    private readonly List<(Task Task, Type? Expected)> _tasks = [];
    private ProviderScope(string? language, uint maximum)
    {
        Boundary.Maximum = maximum;
        var profile = new OcrProviderProfile { Id = "review-local", Kind = OcrProviderKind.WindowsLocal, DisplayName = "Review local", TimeoutSeconds = 120,
            WindowsOptions = new() { LanguageTag = language } };
        Provider = new WindowsOcrProviderFactory(Boundary, Boundary, Time).Create(profile, ProviderCredentials.None);
    }
    internal T Track<T>(T task, Type? expected = null) where T : Task { _tasks.Add((task, expected)); return task; }
    internal bool AllReleased => Boundary.Images.All(i => i.BitmapReleased && i.WriterReleased && i.StreamReleased);
    internal static async Task Run(Func<ProviderScope,Task> body, string? language = null, uint maximum = 10000)
    {
        var f = new ProviderScope(language, maximum); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) try { release(); } catch (Exception ex) { errors.Add(ex); }
            f.Boundary.Before = null; f.Boundary.BeforeRecognition = null; f.Boundary.Failure = null;
            try
            {
                if (f.Provider is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
                else if (f.Provider is IDisposable disposable) disposable.Dispose();
                else errors.Add(new InvalidOperationException("Provider has no disposal path."));
            }
            catch (Exception ex) { errors.Add(ex); }
            foreach (var work in f._tasks)
                try { await work.Task; } catch (Exception ex) { if (work.Expected == null || !work.Expected.IsInstanceOfType(ex)) errors.Add(ex); }
            foreach (var image in f.Boundary.Images) try { await image.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    internal static async Task<ImageData> Input(string directory) => new(await File.ReadAllBytesAsync(Path.Combine(directory,"fixtures","hello-123.png")), ImageFormat.Png,800,180);
    internal static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
}
