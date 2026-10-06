using System.Buffers.Binary;
using System.Net.Http;
using System.Text.Json;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Imaging;
using Shouldly;
using static Iris.Platform.Windows.Tests.Imaging.ImagePreparationTestScope;

namespace Iris.Platform.Windows.Tests.Imaging;

public sealed class FormImageBudgetTests
{
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    [InlineData(63)][InlineData(64)][InlineData(65)][InlineData(256)][InlineData(12289)]
    public async Task ExactSextetCount_MatchesIndependentFormContent(int length)
    {
        foreach (int pattern in new[] { 0, 1, 2, 3 })
        {
            byte[] bytes = Enumerable.Range(0, length).Select(i => pattern switch
            { 0 => (byte)0, 1 => (byte)255, 2 => (byte)251, _ => (byte)(i * 73 + 11) }).ToArray();
            FormUrlEncodedImageLength.Count(bytes, static () => { }).ShouldBe(await ReferenceLength(bytes));
        }
    }

    [Fact]
    public Task NullBudget_PreservesFastAndConversionWithoutCounting() => Run(async f =>
    {
        f.FormBudgetBatch = () => throw new InvalidOperationException("Null budget must not scan.");
        var input = await Image(); var c = Constraints(); c.MaxFormUrlEncodedBytes.ShouldBeNull();
        var (bytes, base64, max, min, formats) = c;
        bytes.ShouldBe(c.MaxEncodedBytes); base64.ShouldBe(c.MaxBytesAppliesToBase64);
        max.ShouldBe(c.MaxLongSide); min.ShouldBe(c.MinShortSide); formats.ShouldBeSameAs(c.AcceptedFormats);
        (await f.Start(input, c)).ShouldBeSameAs(input);
        (await f.Start(input, c with { AcceptedFormats = [ImageFormat.Bmp] })).Format.ShouldBe(ImageFormat.Bmp);
    });

    [Fact]
    public Task OriginalFormOnlyOverLimit_WithoutJpegDoesNotReencode() => Run(async f =>
    {
        var input = await Image();
        var c = Constraints(formats: [ImageFormat.Png]) with { MaxFormUrlEncodedBytes = await ReferenceLength(input.Bytes) - 1 };
        (await Should.ThrowAsync<IrisException>(() => f.Start(input, c, typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Encoders.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(0L)][InlineData(-1L)][InlineData(long.MinValue)]
    public Task InvalidFormBudget_IsRejectedBeforeCodec(long limit) => Run(async f =>
    {
        var input = await Image();
        Should.Throw<IrisException>(() => f.Service.PrepareAsync(input, Constraints() with { MaxFormUrlEncodedBytes = limit }, default)).Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Decoders.ShouldBeEmpty();
    });

    [Fact]
    public Task RealWhiteBmp_OnlyAdditionalBudgetTriggersJpegAndPreservesPixels() => Run(async f =>
    {
        var input = WhiteBmp();
        var c = Constraints(4 * 1024 * 1024, max: 2048, base64: true, formats: [ImageFormat.Bmp, ImageFormat.Jpeg]);
        long formLength = await ReferenceLength(input.Bytes);
        input.Bytes.Length.ShouldBe(2359350); (4 * ((input.Bytes.LongLength + 2) / 3)).ShouldBe(3145800);
        formLength.ShouldBeGreaterThan(8 * 1024 * 1024);
        (await f.Start(input, c)).ShouldBeSameAs(input);
        (await f.Start(input, c with { MaxFormUrlEncodedBytes = formLength })).ShouldBeSameAs(input);
        (await f.Start(input, c with { MaxFormUrlEncodedBytes = long.MaxValue })).ShouldBeSameAs(input);
        var output = await f.Start(input, c with { MaxFormUrlEncodedBytes = 8 * 1024 * 1024 });
        output.ShouldNotBeSameAs(input); output.Format.ShouldBe(ImageFormat.Jpeg);
        output.PixelWidth.ShouldBe(1024); output.PixelHeight.ShouldBe(768);
        long outputForm = await ReferenceLength(output.Bytes);
        outputForm.ShouldBeLessThanOrEqualTo(8 * 1024 * 1024);
        (4 * ((output.Bytes.LongLength + 2) / 3)).ShouldBeLessThanOrEqualTo(c.MaxEncodedBytes);
        (await Pixels(output)).ShouldAllBe(b => b >= 253);
        string directory = Path.Combine(Path.GetTempPath(), "iris-t028a-form-budget"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "white-bmp.json"), JsonSerializer.Serialize(new
        {
            synthetic = true, rawBytes = input.Bytes.Length, base64Bytes = 4 * ((input.Bytes.LongLength + 2) / 3),
            formImageValueBytes = formLength, outputBytes = output.Bytes.Length, outputFormImageValueBytes = outputForm,
            outputWidth = output.PixelWidth, outputHeight = output.PixelHeight, httpRequests = 0
        }));
    });

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public Task SameLengthDifferentDensity_ControlsAllJpegQualities(int successfulAttempt) => Run(async f =>
    {
        f.EncodedSize = _ => 3; f.EncodedBytes = i => i == successfulAttempt ? [0, 0, 0] : [255, 255, 255];
        var input = await Image(); var c = Constraints() with { MaxFormUrlEncodedBytes = 4 };
        var task = f.Start(input, c, successfulAttempt == 5 ? typeof(IrisException) : null);
        if (successfulAttempt == 5) (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.ImageTooLarge);
        else { var output = await task; output.Format.ShouldBe(ImageFormat.Jpeg); (await ReferenceLength(output.Bytes)).ShouldBe(4); }
        f.Encoders.Select(e => (int)Math.Round(e.Quality * 100)).ShouldBe(new[] { 90, 80, 70, 60, 50 }.Take(Math.Min(5, successfulAttempt + 1)));
        foreach (var e in f.Encoders) { e.Source.ShouldBeSameAs(f.Encoders[0].Source); e.StreamDisposed.ShouldBeTrue(); }
    });

    [Theory]
    [InlineData(4L, true)][InlineData(3L, false)]
    public Task FormOnlyBudget_EqualityAndOneLessWithoutJpeg(long budget, bool success) => Run(async f =>
    {
        f.EncodedSize = _ => 3; f.EncodedBytes = _ => [0, 0, 0];
        var input = await Image(format: ImageFormat.Bmp);
        var c = Constraints(formats: [ImageFormat.Png]) with { MaxFormUrlEncodedBytes = budget };
        var task = f.Start(input, c, success ? null : typeof(IrisException));
        if (success) (await task).Format.ShouldBe(ImageFormat.Png);
        else (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Encoders.Count.ShouldBe(1);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task LegacyBudget_IsStillRequiredEvenWhenFormFits(bool base64) => Run(async f =>
    {
        f.EncodedSize = _ => 3; f.EncodedBytes = _ => [0, 0, 0];
        var input = await Image(format: ImageFormat.Bmp);
        var c = Constraints(base64 ? 3 : 2, base64: base64, formats: [ImageFormat.Png]) with { MaxFormUrlEncodedBytes = 100 };
        (await Should.ThrowAsync<IrisException>(() => f.Start(input, c, typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
    });

    [Fact]
    public Task ConstraintSnapshotAndConvertedJpeg_DoNotRepeatNinety() => Run(async f =>
    {
        var formats = new List<ImageFormat> { ImageFormat.Jpeg };
        var c = Constraints() with { AcceptedFormats = formats, MaxFormUrlEncodedBytes = 4 };
        f.EncodedSize = _ => 3; f.EncodedBytes = i => i == 0 ? [255, 255, 255] : [0, 0, 0];
        var gate = f.Gate("D.Write"); var task = f.Start(await Image(), c);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        formats.Clear(); formats.Add(ImageFormat.Png); c = c with { MaxFormUrlEncodedBytes = null };
        gate.Release(); (await task).Format.ShouldBe(ImageFormat.Jpeg);
        f.Encoders.Select(e => e.Quality).ShouldBe(new[] { .9f, .8f });
    });

    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task ActualCountingBatch_CancellationAndStopJoinWork(bool outputCount, bool stop)
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int batches = 0;
            f.Releases.Add(release.Set);
            f.FormBudgetBatch = () => { if (++batches == 2) { entered.TrySetResult(); release.Wait(); } };
            ImageData input = outputCount ? await Image() : WhiteBmp();
            if (outputCount) { f.EncodedSize = _ => 30000; f.EncodedBytes = _ => new byte[30000]; }
            var c = Constraints(4 * 1024 * 1024, max: 2048, formats: outputCount ? [ImageFormat.Jpeg] : [ImageFormat.Bmp, ImageFormat.Jpeg]) with { MaxFormUrlEncodedBytes = long.MaxValue };
            var task = f.Start(input, c, typeof(OperationCanceledException), caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            task.IsCompleted.ShouldBeFalse(); batches.ShouldBe(2);
            if (outputCount) f.Encoders.Single().StreamDisposed.ShouldBeTrue(); else f.Decoders.ShouldBeEmpty();
            Task? stopping = null;
            if (stop) { stopping = f.Service.StopAsync(); stopping.IsCompleted.ShouldBeFalse(); } else caller.Cancel();
            release.Set(); await Should.ThrowAsync<OperationCanceledException>(() => task);
            if (stopping != null) await stopping;
            batches.ShouldBe(2); f.InFlight.ShouldBe(0);
        });
    }

    private static ImageData WhiteBmp()
    {
        const int width = 1024, height = 768, pixels = width * height * 3;
        var bytes = new byte[54 + pixels]; bytes.AsSpan(54).Fill(255);
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), pixels);
        return new(bytes, ImageFormat.Bmp, width, height);
    }
    private static async Task<long> ReferenceLength(byte[] bytes)
    {
        // Each nonfinal chunk contains complete triplets. Framework encoding sees only small strings.
        long total = 0;
        for (int offset = 0; offset < bytes.Length; offset += 768)
        {
            string value = Convert.ToBase64String(bytes, offset, Math.Min(768, bytes.Length - offset));
            using var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("k", value)]);
            total += (await form.ReadAsByteArrayAsync()).Length - 2; // Exclude "k=".
        }
        return total;
    }
}
