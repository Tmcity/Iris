using System.Runtime.CompilerServices;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Platform.Windows.Ocr;
using Shouldly;
using Windows.Graphics.Imaging;
using static Iris.Platform.Windows.Tests.Ocr.WindowsOcrTestScope;

namespace Iris.Platform.Windows.Tests.Ocr;

public sealed class WindowsOcrLifecycleTests
{
    [Fact]
    public async Task RuneResultBatch_CancellationStillOwnsBitmapUntilFormattingEnds()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Result = [new(string.Concat(Enumerable.Repeat("𠀀 𠀁 ", 2000)), [])];
            f.Releases.Add(release.Set);
            f.Before = stage =>
            {
                if (stage == "RECOGNIZE")
                {
                    int checks = 0;
                    f.Time.AfterTimestampRead = () => { if (++checks == 5) { entered.TrySetResult(); release.Wait(); } };
                }
                return Task.CompletedTask;
            };
            var task = f.Recognize(await Image(), expected: typeof(OperationCanceledException), token: caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            caller.Cancel(); task.IsCompleted.ShouldBeFalse(); f.Images.Items.Single().BitmapDisposed.ShouldBeFalse();
            release.Set(); await Should.ThrowAsync<OperationCanceledException>(() => task); f.Images.Items.Single().BitmapDisposed.ShouldBeTrue();
        });
    }

    [Theory]
    [InlineData("WRITE")][InlineData("HEADER")][InlineData("BITMAP")][InlineData("RECOGNIZE")]
    [InlineData("BITMAP_DISPOSE")][InlineData("WRITER_DISPOSE")][InlineData("STREAM_DISPOSE")]
    public Task CallerCancellation_WaitsActualOperationAndReleasesOriginalBitmap(string stage) => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var image = await Image(); var gate = f.Gate(stage);
        var task = f.Recognize(image, expected: typeof(OperationCanceledException), token: caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
        task.IsCompleted.ShouldBeFalse(); f.InFlight.ShouldBe(1);
        if (stage == "RECOGNIZE")
        {
            var frame = f.Images.Items.Single(); frame.BitmapDisposed.ShouldBeFalse();
            using var locked = frame.Bitmap!.LockBuffer(BitmapBufferAccessMode.Read);
            locked.GetPlaneCount().ShouldBe(1);
        }
        gate.Release(); await Should.ThrowAsync<OperationCanceledException>(() => task);
        f.Images.Items.ShouldAllBe(i => i.BitmapDisposed && i.WriterDisposed && i.StreamDisposed);
    });

    [Fact]
    public async Task LanguageCreationCancellation_WaitsForActualBoundaryWithoutCreatingBitmap()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.BeforeLanguage = _ => { entered.TrySetResult(); release.Wait(); }; f.Releases.Add(release.Set);
            var task = f.Recognize(await Image(), expected: typeof(OperationCanceledException), token: caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); task.IsCompleted.ShouldBeFalse();
            release.Set(); await Should.ThrowAsync<OperationCanceledException>(() => task); f.Images.Items.ShouldBeEmpty(); f.Native.RecognitionCalls.ShouldBe(0);
        });
    }

    [Theory]
    [InlineData("WRITE")][InlineData("HEADER")][InlineData("BITMAP")][InlineData("RECOGNIZE")]
    [InlineData("LANGUAGE")]
    public Task UnknownNativeCancellation_IsFailure(string stage) => Run(async f =>
    {
        f.Failures[stage] = new OperationCanceledException("private OCR output"); var image = await Image();
        var task = f.Recognize(image, expected: typeof(IrisException));
        var error = await Should.ThrowAsync<IrisException>(() => task); error.Code.ShouldBe(ErrorCode.Unknown); task.IsCanceled.ShouldBeFalse();
        error.Detail!.ShouldNotContain("private"); error.InnerException.ShouldBeNull();
    });

    [Fact]
    public Task PrimaryAndIndependentCleanupErrors_RetainOriginalResourcesForRetry() => Run(async f =>
    {
        f.Failures["RECOGNIZE"] = new IOException("private recognition", unchecked((int)0x81230001));
        f.Failures["BITMAP_DISPOSE"] = new IOException("private bitmap", unchecked((int)0x81230002));
        f.Failures["STREAM_DISPOSE"] = new IOException("private stream", unchecked((int)0x81230003));
        var image = await Image();
        var error = await Should.ThrowAsync<IrisException>(() => f.Recognize(image, expected: typeof(IrisException)));
        foreach (string value in new[] { "RECOGNIZE", "BITMAP", "STREAM", "81230001", "81230002", "81230003" }) error.Detail!.ShouldContain(value);
        error.Detail!.ShouldNotContain("private"); error.InnerException.ShouldBeNull();
        var original = f.Images.Items.Single(); original.BitmapDisposed.ShouldBeFalse(); original.WriterDisposed.ShouldBeTrue();
        await Should.ThrowAsync<IrisException>(() => f.Recognize(image, expected: typeof(IrisException)));
        f.Native.RecognitionCalls.ShouldBe(1); f.Images.Items.Count.ShouldBe(1);
        f.Failures.Clear(); await f.Provider.RetryCleanupAsync();
        original.BitmapDisposed.ShouldBeTrue(); original.StreamDisposed.ShouldBeTrue(); f.Native.RecognitionCalls.ShouldBe(1);
        await f.Recognize(image); f.Native.RecognitionCalls.ShouldBe(2);
    });

    [Fact]
    public Task RealFailureIsNotHiddenByCancellation() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.Gate("RECOGNIZE");
        f.Failures["RECOGNIZE"] = new IOException("private recognition"); f.Failures["BITMAP_DISPOSE"] = new IOException("private bitmap");
        var task = f.Recognize(await Image(), expected: typeof(IrisException), token: caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); gate.Release();
        var error = await Should.ThrowAsync<IrisException>(() => task); error.Code.ShouldBe(ErrorCode.Unknown); error.Detail!.ShouldContain("RECOGNIZE"); error.Detail!.ShouldContain("BITMAP");
    });

    [Fact]
    public Task QueuedCancellation_DoesNotCreateEngineOrCancelActiveRecognition() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var image = await Image(); var gate = f.Gate("RECOGNIZE");
        var active = f.Recognize(image); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = f.Recognize(image, "ja", typeof(OperationCanceledException), caller.Token);
        await Until(() => f.Provider.WaiterCount == 1); caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        active.IsCompleted.ShouldBeFalse(); f.Native.Engines.Count.ShouldBe(1); gate.Release(); await active;
    });

    [Fact]
    public Task ConcurrentLanguages_AreIndependentAndSerialized() => Run(async f =>
    {
        var image = await Image(); var gate = f.Gate("RECOGNIZE");
        var a = f.Recognize(image, "en"); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var b = f.Recognize(image, "ja"); await Until(() => f.Provider.WaiterCount == 1);
        f.Native.Engines.Count.ShouldBe(1); f.InFlight.ShouldBe(1);
        gate.Release(); await Task.WhenAll(a, b);
        f.Native.Engines.Select(e => e.LanguageTag).ShouldBe(new[] { "en", "ja" }); f.Images.Items.Count.ShouldBe(2);
    });

    [Fact]
    public Task Stop_JoinsInFlightBitmapAndQueuedCallsAndSharesBarrier() => Run(async f =>
    {
        var image = await Image(); var gate = f.Gate("RECOGNIZE");
        var active = f.Recognize(image, expected: typeof(OperationCanceledException)); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = f.Recognize(image, expected: typeof(OperationCanceledException)); await Until(() => f.Provider.WaiterCount == 1);
        Should.Throw<IrisException>(f.Provider.Dispose);
        var stop = f.Provider.DisposeAsync().AsTask(); f.Provider.DisposeAsync().AsTask().ShouldBeSameAs(stop);
        stop.IsCompleted.ShouldBeFalse(); f.Images.Items.Single().BitmapDisposed.ShouldBeFalse();
        Should.Throw<IrisException>(() => f.Provider.RecognizeAsync(new(image), default));
        await Should.ThrowAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        gate.Release(); await stop; await Should.ThrowAsync<OperationCanceledException>(() => active);
        f.Images.Items.Single().BitmapDisposed.ShouldBeTrue(); f.Native.RecognitionCalls.ShouldBe(1); f.Provider.Dispose();
    });

    [Fact]
    public Task TotalDeadline_CoversQueueAndDoesNotAbandonActiveBitmap() => Run(async f =>
    {
        f.Profile = f.Profile with { TimeoutSeconds = 5 };
        var image = await Image(); var gate = f.Gate("RECOGNIZE");
        var active = f.Recognize(image, expected: typeof(IrisException)); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        f.Time.Advance(TimeSpan.FromSeconds(4));
        var queued = f.Recognize(image, expected: typeof(IrisException)); await Until(() => f.Provider.WaiterCount == 1);
        f.Time.Advance(TimeSpan.FromSeconds(5));
        (await Should.ThrowAsync<IrisException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)))).Code.ShouldBe(ErrorCode.Timeout);
        active.IsCompleted.ShouldBeFalse(); f.Images.Items.Single().BitmapDisposed.ShouldBeFalse();
        gate.Release(); (await Should.ThrowAsync<IrisException>(() => active)).Code.ShouldBe(ErrorCode.Timeout);
    });

    [Fact]
    public Task DeadlineTimer_DeductsTimeBeforeWorkerStarts() => Run(async f =>
    {
        f.Profile = f.Profile with { TimeoutSeconds = 5 }; _ = f.Provider;
        f.Time.AfterTimestampRead = () => { f.Time.AfterTimestampRead = null; f.Time.Advance(TimeSpan.FromSeconds(1)); };
        var gate = f.Gate("RECOGNIZE"); var task = f.Recognize(await Image(), expected: typeof(IrisException));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Time.TimerCreated(TimeSpan.FromSeconds(4)).WaitAsync(TimeSpan.FromSeconds(10));
        f.Time.Advance(TimeSpan.FromSeconds(4)); task.IsCompleted.ShouldBeFalse(); gate.Release();
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.Timeout);
    });

    [Fact]
    public Task TimerDisposalFailure_RetriesWithoutRecreatingEngine() => Run(async f =>
    {
        f.Time.FailDispose = true;
        var result = await f.Provider.TestAsync(default); result.Error!.Detail!.ShouldContain("TIMER"); f.Native.Engines.Count.ShouldBe(1);
        await Should.ThrowAsync<IrisException>(() => f.Provider.DisposeAsync().AsTask());
        f.Time.FailDispose = false; await f.Provider.DisposeAsync(); f.Native.Engines.Count.ShouldBe(1);
    });

    [Fact]
    public Task LiveProvider_HoldsNoCompletedImageResultOrEngine() => Run(async f =>
    {
        var references = await Complete(f);
        f.Tasks.Clear(); f.Native.Engines.Clear(); f.Images.Items.Clear(); f.Result = []; await Task.Yield();
        for (int i = 0; i < 5 && references.Any(r => r.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        references.ShouldAllBe(r => !r.IsAlive); GC.KeepAlive(f.Provider);
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> Complete(WindowsOcrTestScope f)
    {
        var input = await Image(); var output = await f.Recognize(input);
        return [new(input), new(input.Bytes), new(output), new(output.Lines), new(output.Lines[0].Text), new(f.Native.Engines.Single()), new(new byte[1000])];
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
}
