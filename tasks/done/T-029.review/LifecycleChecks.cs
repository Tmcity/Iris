using System.Diagnostics;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Platform.Windows.Ocr;
using static ProviderScope;

internal static class LifecycleChecks
{
    internal static async Task Run(string directory, Func<string,Func<Task>,Task> check)
    {
        await check("TOTAL_BUDGET_COVERS_ACTIVE_AND_QUEUED_WORK", () => ProviderScope.Run(async f =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(() => release.TrySetResult());
            f.Boundary.BeforeRecognition = async _ => { entered.TrySetResult(); await release.Task; };
            var input = await Input(directory);
            var first = f.Track(f.Provider.RecognizeAsync(new(input,"en"),default),typeof(IrisException));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = f.Track(f.Provider.RecognizeAsync(new(input,"en"),default),typeof(IrisException));
            await WaitUntil(() => ((WindowsOcrProvider)f.Provider).WaiterCount > 0);
            f.Time.Advance(TimeSpan.FromSeconds(120));
            Require((await Failure(queued.WaitAsync(TimeSpan.FromSeconds(10)))).Code == ErrorCode.Timeout, "queued work reset the profile budget");
            Require(!first.IsCompleted && !f.Boundary.Images.Single().BitmapReleased && f.Boundary.Recognitions == 1, "timeout abandoned active native work or started queued OCR");
            release.TrySetResult();
            Require((await Failure(first)).Code == ErrorCode.Timeout && f.AllReleased, "active timeout did not finish with complete cleanup");
        }));
        foreach (bool stop in new[] { false,true })
            await check("HELD_RECOGNITION_" + (stop ? "STOP" : "CALLER_CANCEL") + "_KEEPS_BITMAP", () => ProviderScope.Run(async f =>
            {
                using var caller = new CancellationTokenSource();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Releases.Add(() => release.TrySetResult());
                f.Boundary.BeforeRecognition = async bitmap =>
                {
                    Require(bitmap.PixelWidth == 800 && bitmap.PixelHeight == 180, "actual SoftwareBitmap shape changed");
                    entered.TrySetResult(); await release.Task;
                    Require(bitmap.PixelWidth == 800, "bitmap became unusable before native recognition completed");
                };
                var work = f.Track(f.Provider.RecognizeAsync(new(await Input(directory),"en"),caller.Token),typeof(OperationCanceledException));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Task? disposal = null;
                if (stop) disposal = f.Track(((IAsyncDisposable)f.Provider).DisposeAsync().AsTask()); else caller.Cancel();
                Require(!work.IsCompleted && !f.Boundary.Images.Single().BitmapReleased, "cancellation completed or disposed bitmap before actual recognition ended");
                if (disposal != null) Require(!disposal.IsCompleted, "async dispose skipped native work");
                release.TrySetResult(); await Canceled(work);
                if (disposal != null) await disposal;
                Require(f.AllReleased && f.Boundary.ActiveRecognitions == 0, "canceled work retained resources");
            }));
        await check("PRIMARY_AND_BITMAP_STREAM_FAILURES_RETRY_SAME_OWNER", () => ProviderScope.Run(async f =>
        {
            f.Boundary.Failure = stage => stage switch
            {
                "recognize" => new IOException("FAKE_NATIVE_PRIVATE",unchecked((int)0x81230101)),
                "bitmap-release" => new IOException("FAKE_BITMAP_PRIVATE",unchecked((int)0x81230102)),
                "stream-release" => new IOException("FAKE_STREAM_PRIVATE",unchecked((int)0x81230103)), _ => null
            };
            var work = f.Track(f.Provider.RecognizeAsync(new(await Input(directory),"en"),default),typeof(IrisException));
            var error = await Failure(work); string detail = error.Detail ?? "";
            Require(error.Code == ErrorCode.Unknown && detail.Contains("81230101") && detail.Contains("81230102") && detail.Contains("81230103") && !detail.Contains("PRIVATE"), "primary/cleanup numeric diagnostics lost or external text exposed");
            var image = f.Boundary.Images.Single(); Require(!image.BitmapReleased && !image.StreamReleased, "fault injection did not retain native resources");
            f.Boundary.Failure = null;
            await f.Track(((WindowsOcrProvider)f.Provider).RetryCleanupAsync());
            Require(f.AllReleased && image.BitmapReleaseCalls == 2 && image.StreamReleaseCalls == 2 && image.WriterReleaseCalls == 1 && f.Boundary.Recognitions == 1 && f.Boundary.Images.Count == 1, "retry replaced owner, repeated OCR, or repeated already released resource");
        }));
        await check("QUEUED_CANCEL_DOES_NOT_CREATE_ENGINE_OR_CANCEL_ACTIVE", () => ProviderScope.Run(async f =>
        {
            using var queuedCaller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(() => release.TrySetResult());
            f.Boundary.BeforeRecognition = async _ => { entered.TrySetResult(); await release.Task; };
            var input = await Input(directory);
            var first = f.Track(f.Provider.RecognizeAsync(new(input,"en"),default));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = f.Track(f.Provider.RecognizeAsync(new(input,"en"),queuedCaller.Token),typeof(OperationCanceledException));
            await WaitUntil(() => ((WindowsOcrProvider)f.Provider).WaiterCount > 0);
            queuedCaller.Cancel(); await Canceled(second.WaitAsync(TimeSpan.FromSeconds(10)));
            Require(f.Boundary.Engines.Count == 1 && f.Boundary.Recognitions == 1 && !first.IsCompleted, "queued cancellation allocated an engine or affected the active call");
            release.TrySetResult(); await first;
            Require(f.AllReleased, "active request did not finish cleanup");
        }));
        await check("CONCURRENT_CALLS_SERIALIZE_ACTUAL_ENGINE_USE", () => ProviderScope.Run(async f =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(() => release.TrySetResult());
            f.Boundary.BeforeRecognition = async _ => { entered.TrySetResult(); await release.Task; };
            var input = await Input(directory);
            var first = f.Track(f.Provider.RecognizeAsync(new(input,"en"),default));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = f.Track(f.Provider.RecognizeAsync(new(input,"zh-Hans"),default));
            var third = f.Track(f.Provider.RecognizeAsync(new(input,"en"),default));
            await WaitUntil(() => ((WindowsOcrProvider)f.Provider).WaiterCount >= 2);
            Require(f.Boundary.Recognitions == 1, "queued requests entered the OCR engine concurrently");
            release.TrySetResult(); await Task.WhenAll(first,second,third);
            Require(f.Boundary.MaximumConcurrency == 1 && f.Boundary.Recognitions == 3 && f.AllReleased, "serialized calls lost work or resources");
        }));
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Actual waiter state was not reached."); await Task.Yield(); }
    }
    private static async Task Canceled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation after actual completion.");
    }
    internal static async Task<IrisException> Failure(Task task)
    {
        try { await task; } catch (IrisException ex) { return ex; }
        throw new InvalidOperationException("Expected Iris failure.");
    }
}
