using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.Capture;
using Shouldly;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Iris.Platform.Windows.Tests.Capture;

public sealed class ScreenCropTests
{
    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Bmp)]
    public Task RealMemoryCodecs_PreservePaddedStrideCropBgrAndTopDownRows(ImageFormat format) => ScreenCaptureTestScope.Run(async f =>
    {
        byte[] input = [1, 2, 3, 7, 4, 5, 6, 8, 7, 8, 9, 9, 90, 90, 90, 90, 10, 11, 12, 1, 13, 14, 15, 2, 16, 17, 18, 3];
        byte[] original = (byte[])input.Clone();
        var frame = new CapturedFrame(input, 3, 2, 16, new(-5, -7, 3, 2), [], DateTimeOffset.UnixEpoch);
        var image = await f.Service.CropAsync(frame, new(-4, -8, 20, 20), format);
        image.PixelWidth.ShouldBe(2); image.PixelHeight.ShouldBe(2); image.Format.ShouldBe(format);
        var decoded = await Decode(image.Bytes);
        decoded.Width.ShouldBe(2u); decoded.Height.ShouldBe(2u);
        decoded.Pixels.ShouldBe(new byte[] { 4, 5, 6, 255, 7, 8, 9, 255, 13, 14, 15, 255, 16, 17, 18, 255 });
        input.ShouldBe(original); byte[] encoded = (byte[])image.Bytes.Clone(); input.AsSpan().Clear(); image.Bytes.ShouldBe(encoded);
        f.Native.Calls.ShouldBeEmpty(); f.Threads.All.ShouldBeEmpty(); f.Desktop.Threads.ShouldBeEmpty();
    }, realEncoder: true);

    [Fact]
    public Task RealJpeg_HasExpectedDimensionsAndReasonableLoss() => ScreenCaptureTestScope.Run(async f =>
    {
        const int width = 64, height = 32;
        var frame = ScreenCaptureTestScope.Frame(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4; frame.Bgra32Pixels[p] = (byte)(x * 3); frame.Bgra32Pixels[p + 1] = (byte)(y * 6);
            frame.Bgra32Pixels[p + 2] = (byte)((x + y) * 2); frame.Bgra32Pixels[p + 3] = 255;
        }
        var image = await f.Service.CropAsync(frame, frame.VirtualBounds, ImageFormat.Jpeg);
        var decoded = await Decode(image.Bytes); decoded.Width.ShouldBe((uint)width); decoded.Height.ShouldBe((uint)height);
        double difference = 0;
        for (int i = 0; i < decoded.Pixels.Length; i++) if (i % 4 != 3) difference += Math.Abs(decoded.Pixels[i] - frame.Bgra32Pixels[i]);
        (difference / (width * height * 3)).ShouldBeLessThan(4.0);
        image.Bytes[0].ShouldBe((byte)0xFF); image.Bytes[1].ShouldBe((byte)0xD8); f.Threads.All.ShouldBeEmpty();
    }, realEncoder: true);

    [Fact]
    public Task CodecParametersAndIndependentPixelInput_AreFixed() => ScreenCaptureTestScope.Run(async f =>
    {
        var frame = ScreenCaptureTestScope.Frame(); frame.Bgra32Pixels.AsSpan().Fill(21);
        var image = await f.Service.CropAsync(frame, frame.VirtualBounds, ImageFormat.Jpeg);
        var io = f.Encoding.Sessions.Single(); io.Quality.ShouldBe(0.9f); io.Format.ShouldBe(ImageFormat.Jpeg);
        io.Width.ShouldBe(2); io.Height.ShouldBe(2); io.Pixels.ShouldNotBeSameAs(frame.Bgra32Pixels);
        io.Pixels!.Where((_, i) => i % 4 == 3).ShouldAllBe(alpha => alpha == 255);
        image.Bytes.ShouldNotBeSameAs(io.Pixels); f.Native.Calls.ShouldBeEmpty();
    });

    [Theory]
    [InlineData("null")][InlineData("buffer")][InlineData("width")][InlineData("height")][InlineData("stride")][InlineData("bounds")][InlineData("short")][InlineData("format")]
    public Task InvalidFramesAndEnums_AreRejectedBeforeEncoderOrWorker(string scenario) => ScreenCaptureTestScope.Run(f =>
    {
        var frame = ScreenCaptureTestScope.Frame();
        frame = scenario switch
        {
            "null" => null!, "buffer" => frame with { Bgra32Pixels = null! }, "width" => frame with { Width = 0 },
            "height" => frame with { Height = -1 }, "stride" => frame with { Stride = 7 }, "bounds" => frame with { VirtualBounds = new(0, 0, 4, 2) },
            "short" => frame with { Bgra32Pixels = [1, 2] }, _ => frame,
        };
        var error = Should.Throw<IrisException>(() => f.Service.CropAsync(frame, new(-3, -2, 2, 2), scenario == "format" ? (ImageFormat)55 : ImageFormat.Png));
        error.Code.ShouldBe(ErrorCode.ImageInvalid); f.Encoding.Sessions.ShouldBeEmpty(); f.Threads.All.ShouldBeEmpty();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(-1, 1)] [InlineData(1, -1)]
    public Task InvalidRegions_AreCaptureFailed(int width, int height) => ScreenCaptureTestScope.Run(f =>
    {
        Should.Throw<IrisException>(() => f.Service.CropAsync(ScreenCaptureTestScope.Frame(), new(-3, -2, width, height))).Code.ShouldBe(ErrorCode.CaptureFailed);
        f.Encoding.Sessions.ShouldBeEmpty(); return Task.CompletedTask;
    });

    [Fact]
    public Task HalfOpenEmptyAndExtremeEndpoints_DoNotOverflowOrReadPadding() => ScreenCaptureTestScope.Run(async f =>
    {
        var frame = ScreenCaptureTestScope.Frame() with { VirtualBounds = new(int.MaxValue - 1, int.MinValue, 2, 2) };
        var image = await f.Service.CropAsync(frame, new(int.MaxValue, int.MinValue, int.MaxValue, int.MaxValue));
        image.PixelWidth.ShouldBe(1); image.PixelHeight.ShouldBe(2);
        Should.Throw<IrisException>(() => f.Service.CropAsync(frame, new(0, 0, 1, 1))).Code.ShouldBe(ErrorCode.CaptureFailed);
        var oneRow = new CapturedFrame([1, 2, 3, 4], 1, 1, int.MaxValue, new(0, 0, 1, 1), [], DateTimeOffset.UnixEpoch);
        (await f.Service.CropAsync(oneRow, new(0, 0, int.MaxValue, int.MaxValue))).PixelWidth.ShouldBe(1);
    });

    [Fact]
    public Task Budgets_AreCheckedWithoutAllocatingGiantFramesOrEncodedOutput() => ScreenCaptureTestScope.Run(f =>
    {
        ScreenPixelLayout.ValidateFrameLayout(32768, 2048, 131072, ScreenPixelLayout.MaxRawBytes, new(0, 0, 32768, 2048));
        ScreenPixelLayout.EncodedLength((ulong)ScreenPixelLayout.MaxEncodedBytes).ShouldBe(ScreenPixelLayout.MaxEncodedBytes);
        Should.Throw<ScreenCaptureException>(() => ScreenPixelLayout.ValidateFrameLayout(1, 1, 4, (long)ScreenPixelLayout.MaxRawBytes + 1, new(0, 0, 1, 1))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        Should.Throw<ScreenCaptureException>(() => ScreenPixelLayout.EncodedLength((ulong)ScreenPixelLayout.MaxEncodedBytes + 1)).Code.ShouldBe(ErrorCode.ImageTooLarge);
        var frame = ScreenCaptureTestScope.Frame() with { Stride = int.MaxValue };
        Should.Throw<IrisException>(() => f.Service.CropAsync(frame, frame.VirtualBounds)).Code.ShouldBe(ErrorCode.ImageTooLarge);
        frame = frame with { Width = 32769, Stride = 4 };
        Should.Throw<IrisException>(() => f.Service.CropAsync(frame, frame.VirtualBounds)).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Encoding.Sessions.ShouldBeEmpty(); return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public Task EncodedBudgetAndShortRead_FailAndDisposeRealOwner(bool oversize) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Encoding.Configure = io => { if (oversize) io.OutputSize = (ulong)ScreenPixelLayout.MaxEncodedBytes + 1; else io.ReadLength = 3; };
        var frame = ScreenCaptureTestScope.Frame();
        (await Should.ThrowAsync<IrisException>(() => f.Service.CropAsync(frame, frame.VirtualBounds))).Code.ShouldBe(oversize ? ErrorCode.ImageTooLarge : ErrorCode.CaptureFailed);
        var io = f.Encoding.Sessions.Single(); io.StreamOwned.ShouldBeFalse(); io.ReaderOwned.ShouldBeFalse();
        if (oversize) io.Calls.ShouldNotContain("Read");
    });

    [Theory]
    [InlineData("Create")][InlineData("Flush")][InlineData("Read")][InlineData("DisposeReader")][InlineData("DisposeStream")]
    public Task CancelDuringCodecStage_WaitsActualOperationAndDisposal(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        using var cts = new CancellationTokenSource(); var gate = f.Gate();
        f.Encoding.Configure = io => io.Before = call => call == stage ? gate.WaitAsync() : Task.CompletedTask;
        var frame = ScreenCaptureTestScope.Frame(); var read = f.Track(f.Service.CropAsync(frame, frame.VirtualBounds, cancellationToken: cts.Token), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cts.Cancel();
        read.IsCompleted.ShouldBeFalse(); Should.Throw<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None)).Detail!.ShouldContain("BUSY");
        var io = f.Encoding.Sessions.Single(); io.InFlight.ShouldBe(1);
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => read);
        io.InFlight.ShouldBe(0); io.StreamOwned.ShouldBeFalse(); io.ReaderOwned.ShouldBeFalse(); f.Threads.All.ShouldBeEmpty();
    });

    [Theory]
    [InlineData("DisposeReader")][InlineData("DisposeStream")]
    public Task CodecCleanupFailure_IsRetainedAndNextOperationRetriesOnlyRemainingStage(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Encoding.Configure = io => { io.ThrowOnCopy = true; io.Failures[stage] = -1; };
        var frame = ScreenCaptureTestScope.Frame();
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CropAsync(frame, frame.VirtualBounds));
        error.Detail!.ShouldContain("ENCODER_READ"); error.Detail!.ShouldContain(stage == "DisposeReader" ? "ENCODER_READER_DISPOSE" : "ENCODER_STREAM_DISPOSE");
        error.ToString().ShouldNotContain("private copied content"); error.InnerException.ShouldBeNull();
        await Should.ThrowAsync<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        f.Native.Calls.ShouldBeEmpty();
        var io = f.Encoding.Sessions.Single(); int before = io.Calls.Count; io.Failures.Clear();
        await f.Service.RetryCleanupAsync(); io.Calls.Skip(before).ShouldBe(new[] { stage });
        await f.Service.CaptureVirtualScreenAsync(CancellationToken.None);
    });

    [Fact]
    public Task StopDuringFlush_IsSharedAndPreCancellationDoesNothing() => ScreenCaptureTestScope.Run(async f =>
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => f.Service.CropAsync(null!, default, (ImageFormat)99, cancelled.Token)); f.Encoding.Sessions.ShouldBeEmpty();
        var gate = f.Gate(); f.Encoding.Configure = io => io.Before = stage => stage == "Flush" ? gate.WaitAsync() : Task.CompletedTask;
        var frame = ScreenCaptureTestScope.Frame(); var crop = f.Track(f.Service.CropAsync(frame, frame.VirtualBounds), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var stop = f.Track(f.Service.StopAsync());
        f.Service.StopAsync().ShouldBeSameAs(stop); crop.IsCompleted.ShouldBeFalse(); stop.IsCompleted.ShouldBeFalse();
        gate.Open(); await stop; await Should.ThrowAsync<OperationCanceledException>(() => crop);
        Should.Throw<IrisException>(() => f.Service.CropAsync(frame, frame.VirtualBounds)).Detail!.ShouldContain("STOPPED");
    });

    [Theory]
    [InlineData("Create")][InlineData("Flush")][InlineData("Read")]
    public Task EncodingFaults_AreSafeAndReleaseAllResources(string stage) => ScreenCaptureTestScope.Run(async f =>
    {
        f.Encoding.Configure = io => io.Failures[stage] = 1;
        var frame = ScreenCaptureTestScope.Frame();
        var error = await Should.ThrowAsync<IrisException>(() => f.Service.CropAsync(frame, frame.VirtualBounds));
        error.Code.ShouldBe(ErrorCode.CaptureFailed); error.ToString().ShouldNotContain("private codec payload");
        f.Encoding.Sessions.Single().InFlight.ShouldBe(0); f.Encoding.Sessions.Single().StreamOwned.ShouldBeFalse();
        f.Encoding.Configure = null; await f.Service.CropAsync(frame, frame.VirtualBounds);
    });

    [Fact]
    public Task ExternalCodecCancellationWithoutCallerCancellation_IsFailure() => ScreenCaptureTestScope.Run(async f =>
    {
        f.Encoding.Configure = io => io.Before = stage => { if (stage == "Flush") throw new OperationCanceledException("private native cancellation"); return Task.CompletedTask; };
        var frame = ScreenCaptureTestScope.Frame();
        using var caller = new CancellationTokenSource();
        var task = f.Service.CropAsync(frame, frame.VirtualBounds, cancellationToken: caller.Token);
        var error = await Should.ThrowAsync<IrisException>(() => task);
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.ToString().ShouldNotContain("private native cancellation");
        caller.IsCancellationRequested.ShouldBeFalse(); task.IsCanceled.ShouldBeFalse();
        f.Encoding.Sessions.Single().InFlight.ShouldBe(0);
        f.Encoding.Sessions.Single().StreamOwned.ShouldBeFalse();
        f.Encoding.Sessions.Single().ReaderOwned.ShouldBeFalse();
        f.Encoding.Sessions.Single().Calls.ShouldContain("DisposeStream");
        f.Encoding.Sessions.Single().Calls.ShouldContain("DisposeReader");
    });

    private static async Task<(uint Width, uint Height, byte[] Pixels)> Decode(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0); var decoder = await BitmapDecoder.CreateAsync(stream);
        var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (decoder.PixelWidth, decoder.PixelHeight, provider.DetachPixelData());
    }
}
