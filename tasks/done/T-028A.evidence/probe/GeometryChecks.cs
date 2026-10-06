using System.Text.Json;
using Iris.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

internal static class GeometryChecks
{
    internal static async Task Run(IImagePreprocessor processor, string directory, Func<string, Func<Task>, Task> check)
    {
        var cases = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "geometry-cases.json")));
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var row = item.Clone();
            await check("ACTUAL_GEOMETRY_" + row.GetProperty("name").GetString(), async () =>
            {
                int Read(string name) => row.GetProperty(name).GetInt32();
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "fixtures", row.GetProperty("input").GetString()!));
                var source = new ImageData(bytes, ImageFormat.Png, Read("width"), Read("height"));
                var original = bytes.ToArray();
                var result = await processor.PrepareAsync(source, new(1_000_000, false, Read("maxLongSide"), Read("minShortSide"), [ImageFormat.Png]), default);
                Require(result.Format == ImageFormat.Png && result.PixelWidth == Read("outputWidth") && result.PixelHeight == Read("outputHeight"), "result metadata does not match independent geometry");
                var decoded = await Decode(result.Bytes);
                Require(decoded.Width == result.PixelWidth && decoded.Height == result.PixelHeight && decoded.Codec == BitmapDecoder.PngDecoderId, "actual encoded result dimensions/codec differ");
                int[] color = row.GetProperty("rgba").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                for (int y = 0; y < decoded.Height; y++)
                for (int x = 0; x < decoded.Width; x++)
                {
                    bool content = x >= Read("leftWhite") && x < Read("leftWhite") + Read("contentWidth") && y >= Read("topWhite") && y < Read("topWhite") + Read("contentHeight");
                    int offset = (y * decoded.Width + x) * 4;
                    int[] expected = content ? [color[2], color[1], color[0], 255] : [255, 255, 255, 255];
                    for (int c = 0; c < 4; c++) Require(Math.Abs(decoded.Pixels[offset + c] - expected[c]) <= 1, $"content/padding mismatch at {x},{y},channel={c}");
                }
                Require(bytes.SequenceEqual(original), "input encoded bytes mutated");
                if (row.GetProperty("name").GetString() == "no_min") Require(ReferenceEquals(source, result), "already satisfied input lost instance identity");
            });
        }
        await check("ACTUAL_ALPHA_WHITE_COMPOSITION", async () =>
        {
            var source = new ImageData(await File.ReadAllBytesAsync(Path.Combine(directory, "fixtures", "alpha.png")), ImageFormat.Png, 3, 1);
            var result = await processor.PrepareAsync(source, new(1_000_000, false, 10, 0, [ImageFormat.Bmp]), default);
            var pixels = await Decode(result.Bytes);
            Require(result.Format == ImageFormat.Bmp && pixels.Codec == BitmapDecoder.BmpDecoderId && pixels.Width == 3 && pixels.Height == 1, "alpha conversion changed shape or format");
            byte[] expected = [255,255,255,255, 127,127,255,255, 0,0,255,255];
            Require(pixels.Pixels.Length == expected.Length, "unexpected decoded stride/length");
            for (int i = 0; i < expected.Length; i++) Require(Math.Abs(pixels.Pixels[i] - expected[i]) <= 1, $"alpha composition mismatch at byte {i}");
        });
    }

    internal static async Task<(int Width, int Height, Guid Codec, byte[] Pixels)> Decode(byte[] encoded)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(encoded);
            await writer.StoreAsync().AsTask().ConfigureAwait(false);
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
        return (checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), decoder.DecoderInformation.CodecId, provider.DetachPixelData());
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
