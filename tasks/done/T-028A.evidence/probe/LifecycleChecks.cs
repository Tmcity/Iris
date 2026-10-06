using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Imaging;

internal static class LifecycleChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        foreach (bool initialJpeg in new[] { false, true })
            await check("QUALITY_SEQUENCE_FROM_ORIGINAL_PIXELS_" + initialJpeg, () => Scope.Run(async f =>
            {
                if (initialJpeg) f.Boundary.Header = f.Boundary.Header with { Format = ImageFormat.Jpeg };
                f.Boundary.EncodedSize = (_, quality) => (ulong)(Math.Round(quality * 100) + 30);
                var result = await f.Prepare(new(90, false, 100, initialJpeg ? 8 : 0, [ImageFormat.Png, ImageFormat.Jpeg]));
                var encoders = f.Boundary.Encoders.ToArray();
                Require(encoders.Select(e => (int)Math.Round(e.Quality * 100)).SequenceEqual(new[] { 90, 80, 70, 60 }), "incorrect or repeated quality attempts");
                Require(encoders.All(e => e.Format == ImageFormat.Jpeg), "over-budget input used a disallowed preliminary format");
                Require(encoders.All(e => ReferenceEquals(e.PixelReference, encoders[0].PixelReference) && e.PixelSnapshot!.SequenceEqual(encoders[0].PixelSnapshot!)), "attempts did not encode the same original prepared pixels");
                Require(result.Bytes.Length == 90 && result.Format == ImageFormat.Jpeg && f.AllReleased, "quality result/cleanup incorrect");
            }));
        await check("JPEG_NOT_ACCEPTED_NEVER_ENCODED", () => Scope.Run(async f =>
        {
            var failure = await Failure(f.Prepare(new(90, false, 100, 0, [ImageFormat.Png]), typeof(IrisException)));
            Require(failure.Code == ErrorCode.ImageTooLarge && f.Boundary.Encoders.IsEmpty && f.AllReleased, "disallowed JPEG escape or lost cleanup");
        }));
        await check("ALL_FIVE_QUALITIES_EXHAUSTED", () => Scope.Run(async f =>
        {
            var failure = await Failure(f.Prepare(new(1, false, 100, 0, [ImageFormat.Png, ImageFormat.Jpeg]), typeof(IrisException)));
            Require(failure.Code == ErrorCode.ImageTooLarge && f.Boundary.Encoders.Select(e => (int)Math.Round(e.Quality * 100)).SequenceEqual(new[] { 90, 80, 70, 60, 50 }) && f.AllReleased, "quality exhaustion violated finite attempts or cleanup");
        }));
        await check("PRIMARY_AND_CLEANUP_SAFE_ERRORS_RETRY_ORIGINAL_RESOURCE", () => Scope.Run(async f =>
        {
            f.Boundary.Before = stage => stage == "flush" ? Task.FromException(new ExternalException("private-input-marker", unchecked((int)0x81230021))) : Task.CompletedTask;
            f.Boundary.BeforeDispose = stage => { if (stage == "encoder-stream") throw new ExternalException("private-cleanup-marker", unchecked((int)0x81230022)); };
            var failure = await Failure(f.Prepare(Convert(), typeof(IrisException)));
            string detail = failure.Detail ?? "";
            Require(failure.Code == ErrorCode.Unknown && detail.Contains("FLUSH") && detail.Contains("ENCODER_STREAM") && detail.Contains("81230021") && detail.Contains("81230022"), "main/cleanup diagnostics lost");
            Require(!failure.ToString().Contains("private-input-marker") && !failure.ToString().Contains("private-cleanup-marker") && !detail.Contains("private-"), "external content leaked");
            var encoder = f.Boundary.Encoders.Single();
            Require(!encoder.StreamDisposed, "failed stream disposal reported success");
            f.Boundary.Before = null; f.Boundary.BeforeDispose = null;
            await f.Track(f.Service.RetryCleanupAsync());
            Require(encoder.StreamDisposed && encoder.StreamDisposeCalls == 2 && f.Boundary.Encoders.Count == 1 && f.AllReleased, "retry did not release original resource without reencoding");
        }));
        foreach (string stage in new[] { "write", "open", "pixels", "create", "flush", "read" })
            await check("CANCEL_WAITS_ACTUAL_" + stage.ToUpperInvariant(), () => Scope.Run(async f =>
            {
                using var caller = new CancellationTokenSource();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Release.Add(() => release.TrySetResult());
                f.Boundary.Before = async current => { if (current == stage) { entered.TrySetResult(); await release.Task; } };
                var task = f.Prepare(Convert(), typeof(OperationCanceledException), caller.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                caller.Cancel();
                Require(!task.IsCompleted, "canceled task completed while native operation still held its resource");
                bool alive = stage is "write" or "open" or "pixels" ? !f.Boundary.Decoders.Single().StreamDisposed : !f.Boundary.Encoders.Single().StreamDisposed;
                Require(alive, "stream disposed before actual operation ended");
                release.TrySetResult();
                await Canceled(task);
                Require(f.AllReleased, "cancellation result preceded actual resource release");
            }));
        await check("CANCEL_DOES_NOT_HIDE_NATIVE_FAILURE", () => Scope.Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Release.Add(() => release.TrySetResult());
            f.Boundary.Before = async stage => { if (stage == "flush") { entered.TrySetResult(); await release.Task; throw new ExternalException("private-native", unchecked((int)0x81230023)); } };
            var task = f.Prepare(Convert(), typeof(IrisException), caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); release.TrySetResult();
            var error = await Failure(task);
            Require(error.Code == ErrorCode.Unknown && (error.Detail ?? "").Contains("81230023") && f.AllReleased, "cancellation replaced actual native failure or cleanup");
        }));
        await check("STOP_WAITS_OPERATION_AND_RETRY_AFTER_STOP_IS_QUIESCENT", () => Scope.Run(async f =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Release.Add(() => release.TrySetResult());
            f.Boundary.Before = async stage => { if (stage == "flush") { entered.TrySetResult(); await release.Task; } };
            var task = f.Prepare(Convert(), typeof(OperationCanceledException));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = f.Track(f.Service.StopAsync());
            Require(!stop.IsCompleted && !task.IsCompleted && !f.Boundary.Encoders.Single().StreamDisposed, "stop completed before accepted work and resources ended");
            release.TrySetResult(); await Canceled(task); await stop;
            await f.Track(f.Service.StopAsync()); await f.Track(f.Service.RetryCleanupAsync());
            Require(f.AllReleased, "successful stop/retry retained resources");
        }));
        await check("QUEUED_CALLER_CANCEL_DOES_NOT_CANCEL_ACTIVE_REQUEST", () => Scope.Run(async f =>
        {
            using var queuedCaller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Release.Add(() => release.TrySetResult());
            f.Boundary.Before = async stage => { if (stage == "write") { entered.TrySetResult(); await release.Task; } };
            var first = f.Prepare(Convert());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = f.Prepare(Convert(), typeof(OperationCanceledException), queuedCaller.Token);
            queuedCaller.Cancel(); await Canceled(queued.WaitAsync(TimeSpan.FromSeconds(10)));
            Require(!first.IsCompleted && f.Boundary.Decoders.Count == 1, "queued cancellation altered active work or allocated codec");
            release.TrySetResult(); await first;
            Require(f.AllReleased, "active request failed to release resources");
        }));
    }
    private static ImageConstraints Convert() => new(1_000, false, 100, 0, [ImageFormat.Bmp]);
    private static void Require(bool condition, string message) => GeometryChecks.Require(condition, message);
    private static async Task<IrisException> Failure(Task task)
    {
        try { await task; } catch (IrisException ex) { return ex; }
        throw new InvalidOperationException("Expected a visible Iris failure.");
    }
    private static async Task Canceled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation after complete cleanup.");
    }
    private sealed class Scope
    {
        internal readonly PreparationBoundary Boundary = new();
        internal readonly WinRtImagePreprocessor Service;
        internal readonly List<Action> Release = [];
        private readonly List<(Task Task, Type? Expected)> _tasks = [];
        private Scope() { Service = new(Boundary); }
        internal bool AllReleased => Boundary.Decoders.All(d => d.WriterDisposed && d.StreamDisposed) && Boundary.Encoders.All(e => e.ReaderDisposed && e.StreamDisposed);
        internal Task<ImageData> Prepare(ImageConstraints c, Type? expected = null, CancellationToken token = default) => Track(Service.PrepareAsync(Boundary.Input, c, token), expected);
        internal T Track<T>(T task, Type? expected = null) where T : Task { _tasks.Add((task, expected)); return task; }
        internal static async Task Run(Func<Scope, Task> body)
        {
            var f = new Scope(); Exception? primary = null; var errors = new List<Exception>();
            try { await body(f); } catch (Exception ex) { primary = ex; }
            finally
            {
                foreach (var release in f.Release) try { release(); } catch (Exception ex) { errors.Add(ex); }
                f.Boundary.Before = null; f.Boundary.BeforeDispose = null;
                try { await f.Service.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
                foreach (var work in f._tasks)
                    try { await work.Task; } catch (Exception ex) { if (work.Expected == null || !work.Expected.IsInstanceOfType(ex)) errors.Add(ex); }
                try { await f.Service.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            }
            if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) errors.Insert(0, primary);
            if (errors.Count != 0) throw new AggregateException(errors);
        }
    }
}
