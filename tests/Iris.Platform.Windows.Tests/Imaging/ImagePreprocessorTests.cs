using Iris.Core.Errors;
using Iris.Core.Imaging;
using Shouldly;
using static Iris.Platform.Windows.Tests.Imaging.ImagePreparationTestScope;

namespace Iris.Platform.Windows.Tests.Imaging;

public sealed class ImagePreprocessorTests
{
    [Fact]
    public Task FantDownscale_PremultipliedTransparencyDoesNotBleedHiddenColor() => Run(async f =>
    {
        var input = await Image(2, 1, pixels: [255, 0, 255, 0, 0, 0, 0, 255]);
        var output = await f.Start(input, Constraints(max: 1));
        var pixels = await Pixels(output);
        pixels[0].ShouldBeInRange((byte)120, (byte)135);
        pixels[0].ShouldBe(pixels[1]); pixels[1].ShouldBe(pixels[2]); pixels[3].ShouldBe((byte)255);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task OriginalByteBudget_UsesExactLengthOnFastPath(bool base64) => Run(async f =>
    {
        var input = await Image(); long n = input.Bytes.Length;
        long limit = base64 ? 4 * ((n + 2) / 3) : n;
        (await f.Start(input, Constraints(limit, base64: base64, formats: [ImageFormat.Png]))).ShouldBeSameAs(input);
        (await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(limit - 1, base64: base64, formats: [ImageFormat.Png]), typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Encoders.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(ImageFormat.Png)][InlineData(ImageFormat.Jpeg)][InlineData(ImageFormat.Bmp)]
    public Task ValidFastPath_ActuallyDecodesAndReturnsSameInstance(ImageFormat format) => Run(async f =>
    {
        var image = await Image(format: format); var before = image.Bytes.ToArray();
        (await f.Start(image, Constraints())).ShouldBeSameAs(image);
        image.Bytes.ShouldBe(before); f.Decoders.Single().StreamDisposed.ShouldBeTrue(); f.Encoders.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(ImageFormat.Png, ImageFormat.Jpeg)][InlineData(ImageFormat.Png, ImageFormat.Bmp)]
    [InlineData(ImageFormat.Jpeg, ImageFormat.Png)][InlineData(ImageFormat.Jpeg, ImageFormat.Bmp)]
    [InlineData(ImageFormat.Bmp, ImageFormat.Png)][InlineData(ImageFormat.Bmp, ImageFormat.Jpeg)]
    public Task RealFormats_EncodeAndDecodePixels(ImageFormat from, ImageFormat to) => Run(async f =>
    {
        var result = await f.Start(await Image(format: from), Constraints(formats: [to]));
        result.Format.ShouldBe(to); (await Pixels(result)).Where((_, i) => i % 4 != 3).ShouldAllBe(b => b <= 2);
        f.Encoders.Single().StreamDisposed.ShouldBeTrue();
    });

    [Theory]
    [InlineData(20, 10, 100, 40, 80, 40, 80, 40)]
    [InlineData(1000, 500, 100, 40, 100, 50, 100, 50)]
    [InlineData(40, 10, 100, 40, 100, 25, 100, 40)]
    [InlineData(10, 40, 100, 40, 25, 100, 40, 100)]
    [InlineData(5, 5, 100, 0, 5, 5, 5, 5)]
    [InlineData(32768, 1, 100, 40, 100, 1, 100, 40)]
    [InlineData(1, 32768, 100, 40, 1, 100, 40, 100)]
    [InlineData(17, 7, 13, 5, 13, 5, 13, 5)]
    public Task RationalResizeAndWhiteCanvas_PreserveContent(int w, int h, int max, int min, int cw, int ch, int ow, int oh) => Run(async f =>
    {
        var result = await f.Start(await Image(w, h), Constraints(max: max, min: min));
        result.PixelWidth.ShouldBe(ow); result.PixelHeight.ShouldBe(oh);
        var pixels = await Pixels(result); int left = (ow - cw) / 2, top = (oh - ch) / 2;
        for (int y = 0; y < oh; y++) for (int x = 0; x < ow; x++)
        {
            bool content = x >= left && x < left + cw && y >= top && y < top + ch;
            int at = (y * ow + x) * 4;
            pixels[at].ShouldBe(content ? (byte)0 : (byte)255); pixels[at + 3].ShouldBe((byte)255);
        }
    });

    [Fact]
    public Task StraightAlpha_CompositesWhiteAndFastPathKeepsOriginal() => Run(async f =>
    {
        var input = await Image(3, 1, pixels: [200, 100, 50, 0, 40, 80, 160, 128, 10, 20, 30, 255]);
        (await f.Start(input, Constraints())).ShouldBeSameAs(input);
        var result = await f.Start(input, Constraints(formats: [ImageFormat.Bmp]));
        var pixels = await Pixels(result);
        pixels[..4].ShouldBe(new byte[] { 255, 255, 255, 255 });
        for (int c = 0; c < 3; c++) Math.Abs(pixels[4 + c] - ((new[] { 40, 80, 160 }[c] * 128 + 255 * 127 + 127) / 255)).ShouldBeLessThanOrEqualTo(1);
        pixels[7].ShouldBe((byte)255); pixels[8..].ShouldBe(new byte[] { 10, 20, 30, 255 });
    });

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public Task InvalidConstraints_AreRejectedWithoutCodec(int kind) => Run(async f =>
    {
        var c = kind switch
        {
            0 => Constraints(bytes: 0), 1 => Constraints(max: 0), 2 => Constraints(min: -1),
            3 => Constraints(max: 4, min: 5), 4 => Constraints() with { AcceptedFormats = [] },
            _ => Constraints(formats: [(ImageFormat)88])
        };
        Should.Throw<IrisException>(() => f.Service.PrepareAsync(awaitImage, c, default)).Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Decoders.ShouldBeEmpty(); await Task.CompletedTask;
    });
    private static readonly ImageData awaitImage = new([1], ImageFormat.Png, 1, 1);

    [Theory]
    [InlineData("empty", ErrorCode.ImageInvalid)][InlineData("format", ErrorCode.ImageInvalid)]
    [InlineData("zero", ErrorCode.ImageInvalid)][InlineData("large", ErrorCode.ImageTooLarge)]
    [InlineData("pixels", ErrorCode.ImageTooLarge)][InlineData("corrupt", ErrorCode.ImageInvalid)]
    [InlineData("truncated", ErrorCode.ImageInvalid)][InlineData("mismatch", ErrorCode.ImageInvalid)]
    [InlineData("codec", ErrorCode.ImageInvalid)]
    public Task InvalidInput_IsSafe(string kind, ErrorCode code) => Run(async f =>
    {
        var original = await Image();
        var image = kind switch
        {
            "empty" => original with { Bytes = [] }, "format" => original with { Format = (ImageFormat)99 },
            "zero" => original with { PixelWidth = 0 }, "large" => original with { PixelWidth = 32769 },
            "pixels" => original with { PixelWidth = 32768, PixelHeight = 32768 },
            "corrupt" => original with { Bytes = [1, 2, 3] }, "truncated" => original with { Bytes = original.Bytes[..(original.Bytes.Length / 2)] },
            "mismatch" => original with { PixelWidth = 3 }, _ => original with { Format = ImageFormat.Jpeg }
        };
        var error = await Should.ThrowAsync<IrisException>(() => f.Start(image, Constraints(), expected: typeof(IrisException)));
        error.Code.ShouldBe(code); error.InnerException.ShouldBeNull();
        if (kind is "empty" or "format" or "zero" or "large" or "pixels") f.Decoders.ShouldBeEmpty();
    });

    [Fact]
    public Task HeaderAndCanvasBudgets_RejectBeforePixelAllocation() => Run(async f =>
    {
        var image = await Image(); f.Header = new(ImageFormat.Png, 32768, 32768);
        (await Should.ThrowAsync<IrisException>(() => f.Start(image, Constraints(), expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        Should.Throw<IrisException>(() => f.Service.PrepareAsync(image, Constraints(max: 100000, min: 100000), default)).Code.ShouldBe(ErrorCode.ImageTooLarge);
    });

    [Theory]
    [InlineData(3, false)][InlineData(4, false)][InlineData(5, false)]
    [InlineData(3, true)][InlineData(4, true)][InlineData(5, true)]
    public Task ExactBudget_AcceptsEqualityRejectsOneLess(int size, bool base64) => Run(async f =>
    {
        f.EncodedSize = _ => (ulong)size;
        long budget = base64 ? 4 * ((size + 2) / 3) : size;
        var input = await Image(format: ImageFormat.Bmp);
        (await f.Start(input, Constraints(budget, base64: base64, formats: [ImageFormat.Png]))).Bytes.Length.ShouldBe(size);
        (await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(budget - 1, base64: base64, formats: [ImageFormat.Png]), expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Encoders.Count.ShouldBe(2);
    });

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public Task JpegLadder_UsesEachQualityOnceAndOriginalPixels(int acceptedAttempt) => Run(async f =>
    {
        f.EncodedSize = i => i == acceptedAttempt ? 5UL : 11UL;
        var input = await Image();
        var task = f.Start(input, Constraints(bytes: 10, formats: [ImageFormat.Png, ImageFormat.Jpeg]), expected: acceptedAttempt == 5 ? typeof(IrisException) : null);
        if (acceptedAttempt == 5) (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.ImageTooLarge);
        else (await task).Format.ShouldBe(ImageFormat.Jpeg);
        f.Encoders.Select(e => (int)Math.Round(e.Quality * 100)).ShouldBe(new[] { 90, 80, 70, 60, 50 }.Take(Math.Min(acceptedAttempt + 1, 5)));
        foreach (var e in f.Encoders) { e.Source.ShouldBeSameAs(f.Encoders[0].Source); e.StreamDisposed.ShouldBeTrue(); }
    });

    [Fact]
    public Task PreferredFormatAndConvertedJpeg_DoNotRepeatNinety() => Run(async f =>
    {
        f.EncodedSize = i => i == 0 ? 12UL : 8UL;
        var result = await f.Start(await Image(format: ImageFormat.Png), Constraints(bytes: 10, formats: [ImageFormat.Jpeg, ImageFormat.Bmp, ImageFormat.Jpeg]));
        result.Format.ShouldBe(ImageFormat.Jpeg); f.Encoders.Select(e => e.Quality).ShouldBe(new[] { .9f, .8f });
    });

    [Fact]
    public Task AcceptedOriginalFormat_HasPriorityOverListAndNonJpegCannotEscapeBudget() => Run(async f =>
    {
        var input = await Image();
        (await f.Start(input, Constraints(max: 2, formats: [ImageFormat.Bmp, ImageFormat.Png]))).Format.ShouldBe(ImageFormat.Png);
        int encoders = f.Encoders.Count;
        (await Should.ThrowAsync<IrisException>(() => f.Start(input, Constraints(bytes: 1, formats: [ImageFormat.Png]), expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.ImageTooLarge);
        f.Encoders.Count.ShouldBe(encoders);
    });
}
