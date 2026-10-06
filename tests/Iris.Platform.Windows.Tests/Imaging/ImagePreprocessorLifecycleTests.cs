using System.Runtime.CompilerServices;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Imaging;
using Shouldly;
using static Iris.Platform.Windows.Tests.Imaging.ImagePreparationTestScope;

namespace Iris.Platform.Windows.Tests.Imaging;

public sealed class ImagePreprocessorLifecycleTests
{
    [Fact]
    public Task NewRequest_RetriesRetainedResourceBeforeAnyNewCodec() => Run(async f =>
    {
        var input = await Image(); f.Failures["E.Stream"] = new IOException("private cleanup");
        await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(formats: [ImageFormat.Bmp]), typeof(IrisException)));
        var original = f.Encoders.Single();
        await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(), typeof(IrisException)));
        f.Decoders.Count.ShouldBe(1); f.Encoders.Count.ShouldBe(1); original.StreamDisposed.ShouldBeFalse();
        f.Failures.Clear(); (await f.Start(input, Constraints())).ShouldBeSameAs(input);
        original.StreamDisposed.ShouldBeTrue(); f.Encoders.Count.ShouldBe(1); f.Decoders.Count.ShouldBe(2);
    });

    [Theory]
    [InlineData("D.Write")][InlineData("D.Open")][InlineData("D.Pixels")]
    [InlineData("D.Writer")][InlineData("D.Stream")][InlineData("E.Create")]
    [InlineData("E.Flush")][InlineData("E.Read")][InlineData("E.Reader")][InlineData("E.Stream")]
    public Task CallerCancellation_WaitsForActualBoundaryAndCleanup(string stage) => Run(async f =>
    {
        using var caller = new CancellationTokenSource();
        var gate = f.Gate(stage);
        var task = f.Start(await Image(), Constraints(formats: [ImageFormat.Bmp]), typeof(OperationCanceledException), caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
        task.IsCompleted.ShouldBeFalse(); f.InFlight.ShouldBe(1);
        gate.Release(); await Should.ThrowAsync<OperationCanceledException>(() => task);
        f.InFlight.ShouldBe(0); f.Decoders.ShouldAllBe(d => d.WriterDisposed && d.StreamDisposed);
        f.Encoders.ShouldAllBe(e => e.ReaderDisposed && e.StreamDisposed);
    });

    [Theory]
    [InlineData("D.Write")][InlineData("D.Open")][InlineData("D.Pixels")]
    [InlineData("E.Create")][InlineData("E.Flush")][InlineData("E.Read")]
    public Task UnrequestedCodecCancellation_IsSafeFailure(string stage) => Run(async f =>
    {
        f.Failures[stage] = new OperationCanceledException("private pixels");
        var task = f.Start(await Image(), Constraints(formats: [ImageFormat.Bmp]), typeof(IrisException));
        var error = await Should.ThrowAsync<IrisException>(() => task);
        error.Code.ShouldBe(ErrorCode.Unknown); task.IsCanceled.ShouldBeFalse();
        error.Message.ShouldNotContain("private"); error.Detail!.ShouldNotContain("private"); error.InnerException.ShouldBeNull();
    });

    [Fact]
    public Task PrimaryAndMultipleCleanupErrors_RetryOriginalResourcesWithoutReencoding() => Run(async f =>
    {
        f.Failures["E.Flush"] = new IOException("private flush", unchecked((int)0x81230001));
        f.Failures["E.Reader"] = new IOException("private reader", unchecked((int)0x81230002));
        f.Failures["E.Stream"] = new IOException("private stream", unchecked((int)0x81230003));
        var input = await Image();
        var error = await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(formats: [ImageFormat.Bmp]), typeof(IrisException)));
        error.Code.ShouldBe(ErrorCode.Unknown);
        foreach (string value in new[] { "FLUSH", "ENCODER_READER", "ENCODER_STREAM", "81230001", "81230002", "81230003" }) error.Detail!.ShouldContain(value);
        error.Detail!.ShouldNotContain("private");
        var encoder = f.Encoders.Single(); encoder.StreamDisposed.ShouldBeFalse();
        await Should.ThrowAsync<IrisException>(() => f.Service.RetryCleanupAsync());
        f.Failures.Clear(); await f.Service.RetryCleanupAsync();
        f.Encoders.Single().ShouldBeSameAs(encoder); encoder.StreamDisposed.ShouldBeTrue(); encoder.ReaderDisposed.ShouldBeTrue();
        (await f.Start(await Image(), Constraints())).ShouldNotBeNull();
    });

    [Fact]
    public Task CancelAndRealFailure_PreserveFailureAndCleanup() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.Gate("E.Flush");
        f.Failures["E.Flush"] = new IOException("private failure");
        f.Failures["E.Stream"] = new IOException("private cleanup");
        var task = f.Start(await Image(), Constraints(formats: [ImageFormat.Bmp]), typeof(IrisException), caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); gate.Release();
        var error = await Should.ThrowAsync<IrisException>(() => task);
        error.Detail!.ShouldContain("FLUSH"); error.Detail!.ShouldContain("ENCODER_STREAM"); error.Detail!.ShouldContain("cancelled=True");
        task.IsCanceled.ShouldBeFalse();
    });

    [Fact]
    public Task QueuedCancellation_DoesNotCancelActiveOrStartAnotherDecoder() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.Gate("D.Write");
        var input = await Image(); var active = f.Start(input, Constraints());
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = f.Start(input, Constraints(), typeof(OperationCanceledException), caller.Token);
        caller.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        f.Decoders.Count.ShouldBe(1); active.IsCompleted.ShouldBeFalse(); gate.Release(); (await active).ShouldBeSameAs(input);
    });

    [Fact]
    public Task Stop_JoinsActiveAndQueuedCallsAndSharesBarrier() => Run(async f =>
    {
        var gate = f.Gate("E.Flush"); var input = await Image();
        var active = f.Start(input, Constraints(formats: [ImageFormat.Bmp]), typeof(OperationCanceledException));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = f.Start(input, Constraints(), typeof(OperationCanceledException));
        var stop = f.Service.StopAsync(); f.Service.StopAsync().ShouldBeSameAs(stop);
        stop.IsCompleted.ShouldBeFalse();
        Should.Throw<IrisException>(() => f.Service.PrepareAsync(input, Constraints(), default));
        gate.Release(); await stop;
        await Should.ThrowAsync<OperationCanceledException>(() => active); await Should.ThrowAsync<OperationCanceledException>(() => queued);
        f.Decoders.Count.ShouldBe(1); f.Encoders.Single().StreamDisposed.ShouldBeTrue();
    });

    [Fact]
    public Task ConcurrentCallsAndConstraintSnapshot_AreIndependent() => Run(async f =>
    {
        var input = await Image(); var formats = new List<ImageFormat> { ImageFormat.Bmp };
        var gate = f.Gate("D.Write");
        var first = f.Start(input, Constraints() with { AcceptedFormats = formats });
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var second = f.Start(input, Constraints(formats: [ImageFormat.Jpeg]));
        formats.Clear(); formats.Add(ImageFormat.Png); gate.Release();
        (await first).Format.ShouldBe(ImageFormat.Bmp); (await second).Format.ShouldBe(ImageFormat.Jpeg);
        f.Decoders.Count.ShouldBe(2); f.Encoders.Count.ShouldBe(2);
    });

    [Theory]
    [InlineData(0UL, null)][InlineData(285212673UL, null)][InlineData(4UL, 3U)]
    public Task OutputLength_IsCheckedBeforeAllocationAndAfterRead(ulong size, uint? read) => Run(async f =>
    {
        f.EncodedSize = _ => size; f.ReadLength = read;
        var input = await Image();
        var error = await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(formats: [ImageFormat.Bmp]), typeof(IrisException)));
        error.Code.ShouldBe(size > 285212672 ? ErrorCode.ImageTooLarge : ErrorCode.Unknown);
    });

    [Fact]
    public Task AlreadyCanceledAndUnusedStop_CreateNoCodec() => Run(async f =>
    {
        var input = await Image();
        Should.Throw<OperationCanceledException>(() => f.Service.PrepareAsync(input, Constraints(), new CancellationToken(true)));
        await f.Service.StopAsync(); f.Decoders.ShouldBeEmpty(); f.Encoders.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CompletedResult_IsNotCachedOnLiveService(bool convert)
    {
        await using var service = new WinRtImagePreprocessor(new WinRtImagePreparationFactory());
        var references = await Complete(service, convert);
        await Task.Yield();
        for (int i = 0; i < 5 && references.Any(w => w.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        references.ShouldAllBe(w => !w.IsAlive);
        GC.KeepAlive(service);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> Complete(WinRtImagePreprocessor service, bool convert)
    {
        var input = await Image();
        var output = await service.PrepareAsync(input, Constraints(formats: [convert ? ImageFormat.Bmp : ImageFormat.Png]), CancellationToken.None);
        return [new(input), new(input.Bytes), new(output), new(output.Bytes), new(new byte[1024])];
    }

    [Theory]
    [InlineData("D.Open")][InlineData("D.Pixels")]
    public Task DecoderInfrastructureFailure_IsUnknownAndRedacted(string stage) => Run(async f =>
    {
        f.Failures[stage] = new IrisException(ErrorCode.ImageInvalid, "private-message", new IOException("private-inner"), detail: "private-detail");
        var input = await Image();
        var error = await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(), typeof(IrisException)));
        error.Code.ShouldBe(ErrorCode.Unknown); error.Detail!.ShouldNotContain("private"); error.InnerException.ShouldBeNull();
    });
}
