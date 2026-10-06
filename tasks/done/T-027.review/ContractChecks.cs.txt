using Iris.Core.Errors;
using Iris.Core.Imaging;
using Windows.Graphics.Imaging;

internal static class ContractChecks
{
    internal static async Task Run(IImagePreprocessor processor, string directory, Func<string, Func<Task>, Task> check)
    {
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp })
        {
            await check("ACCEPTED_" + format + "_RETURNS_SAME_INSTANCE", async () =>
            {
                var source = await Load(format);
                var result = await processor.PrepareAsync(source, new(1_000_000, false, 100, 0, [ImageFormat.Bmp, ImageFormat.Jpeg, ImageFormat.Png]), default);
                GeometryChecks.Require(ReferenceEquals(source, result), "accepted format lost original instance despite all constraints being satisfied");
            });
        }
        foreach (var format in new[] { ImageFormat.Bmp, ImageFormat.Jpeg })
        {
            await check("UNACCEPTED_PNG_USES_FIRST_" + format, async () =>
            {
                var source = await Load(ImageFormat.Png);
                var result = await processor.PrepareAsync(source, new(1_000_000, false, 100, 0, [format, format == ImageFormat.Bmp ? ImageFormat.Jpeg : ImageFormat.Bmp]), default);
                var actual = await GeometryChecks.Decode(result.Bytes);
                Guid codec = format == ImageFormat.Bmp ? BitmapDecoder.BmpDecoderId : BitmapDecoder.JpegDecoderId;
                GeometryChecks.Require(result.Format == format && actual.Codec == codec && actual.Width == 17 && actual.Height == 9, "first accepted format was not actually encoded");
            });
        }
        foreach (int remainder in new[] { 0, 1, 2 })
        foreach (bool base64 in new[] { false, true })
        {
            await check($"EXACT_{(base64 ? "BASE64" : "RAW")}_BUDGET_REMAINDER_{remainder}", async () =>
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "fixtures", $"budget-mod{remainder}.png"));
                GeometryChecks.Require(bytes.Length % 3 == remainder, "fixture remainder mismatch");
                var source = new ImageData(bytes, ImageFormat.Png, 17, 9);
                long exact = base64 ? 4 * ((bytes.LongLength + 2) / 3) : bytes.LongLength;
                var result = await processor.PrepareAsync(source, new(exact, base64, 100, 0, [ImageFormat.Png]), default);
                GeometryChecks.Require(ReferenceEquals(source, result), "equal exact encoded budget was rejected/reencoded");
                await Expect(ErrorCode.ImageTooLarge, () => processor.PrepareAsync(source, new(exact - 1, base64, 100, 0, [ImageFormat.Png]), default));
            });
        }
        await check("INVALID_CONTENT_AND_METADATA_FAIL_AND_RECOVER", async () =>
        {
            var valid = await Load(ImageFormat.Png);
            var constraints = new ImageConstraints(1_000_000, false, 100, 0, [ImageFormat.Png, ImageFormat.Bmp, ImageFormat.Jpeg]);
            foreach (var invalid in new[]
            {
                valid with { Format = ImageFormat.Jpeg },
                valid with { PixelWidth = 18 },
                valid with { Bytes = valid.Bytes.Take(20).ToArray() },
                valid with { Bytes = [1, 2, 3, 4] },
                valid with { Bytes = [] }
            }) await Expect(ErrorCode.ImageInvalid, () => processor.PrepareAsync(invalid, constraints, default));
            var result = await processor.PrepareAsync(valid, constraints, default);
            GeometryChecks.Require(ReferenceEquals(valid, result), "invalid requests poisoned subsequent valid preparation");
        });
        await check("INVALID_CONSTRAINTS_REJECTED", async () =>
        {
            var source = await Load(ImageFormat.Png);
            var valid = new ImageConstraints(1_000_000, false, 100, 0, [ImageFormat.Png]);
            foreach (var invalid in new[]
            {
                valid with { MaxEncodedBytes = 0 }, valid with { MaxLongSide = 0 },
                valid with { MinShortSide = -1 }, valid with { MinShortSide = 101 },
                valid with { AcceptedFormats = [] }, valid with { AcceptedFormats = [(ImageFormat)999] }
            }) await Expect(ErrorCode.InvalidArgument, () => processor.PrepareAsync(source, invalid, default));
        });
        await check("PRECANCELED_CALL_DOES_NOT_STOP_INSTANCE", async () =>
        {
            var source = await Load(ImageFormat.Png);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = false;
            try { await processor.PrepareAsync(source, new(1_000_000, false, 100, 0, [ImageFormat.Png]), cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            GeometryChecks.Require(canceled, "pre-canceled call returned a successful result");
            var result = await processor.PrepareAsync(source, new(1_000_000, false, 100, 0, [ImageFormat.Png]), default);
            GeometryChecks.Require(ReferenceEquals(source, result), "caller cancellation stopped the shared instance");
        });
        await check("CONCURRENT_CALLS_PRESERVE_INDIVIDUAL_IDENTITY", async () =>
        {
            var original = await Load(ImageFormat.Png);
            var inputs = Enumerable.Range(0, 8).Select(_ => original with { Bytes = original.Bytes.ToArray() }).ToArray();
            var tasks = inputs.Select(input => processor.PrepareAsync(input, new(1_000_000, false, 100, 0, [ImageFormat.Png]), default)).ToArray();
            var results = await Task.WhenAll(tasks);
            for (int i = 0; i < inputs.Length; i++) GeometryChecks.Require(ReferenceEquals(inputs[i], results[i]), "parallel call returned another request's instance");
        });

        async Task<ImageData> Load(ImageFormat format) => new(await File.ReadAllBytesAsync(Path.Combine(directory, "fixtures", "format." + format.ToString().ToLowerInvariant())), format, 17, 9);
    }

    internal static async Task Expect(ErrorCode expected, Func<Task<ImageData>> action)
    {
        try { await action(); }
        catch (IrisException ex)
        {
            GeometryChecks.Require(ex.Code == expected, $"expected {expected}, received {ex.Code}");
            return;
        }
        throw new InvalidOperationException($"Expected {expected}, but preparation succeeded.");
    }
}
