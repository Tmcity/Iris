using Iris.Core.Errors;
using Iris.Core.Imaging;

namespace Iris.Platform.Windows.Imaging;

internal sealed class ImagePreparationException(ErrorCode code, string detail)
    : IrisException(code, ErrorMessages.GetDefault(code), detail: detail);

internal readonly record struct ImagePreparationLayout(int Width, int Height, int CanvasWidth, int CanvasHeight);

internal static class ImagePreparationRules
{
    internal const long MaxEncoded = 285_212_672;
    internal static ImagePreparationException Fail(string stage, ErrorCode code = ErrorCode.Unknown) => new(code, $"stage={stage}");
    internal static string Describe(string stage, Exception ex) => ex switch
    {
        ImagePreparationException known => $"{stage}[{known.Detail}]",
        AggregateException aggregate => $"{stage}[{string.Join(';', aggregate.InnerExceptions.Select(e => Describe("ERROR", e)))}]",
        _ => $"stage={stage};hr=0x{ex.HResult:X8}"
    };
    internal static int PixelBytes(long width, long height)
    {
        if (width <= 0 || height <= 0) throw Fail("DIMENSIONS", ErrorCode.ImageInvalid);
        if (width > 32768 || height > 32768 || width * height > 67_108_864)
            throw Fail("PIXEL_BUDGET", ErrorCode.ImageTooLarge);
        return checked((int)(width * height * 4));
    }
    internal static ImageConstraints Snapshot(ImageConstraints c)
    {
        if (c == null || c.MaxEncodedBytes <= 0 || c.MaxLongSide < 1 || c.MinShortSide < 0 ||
            c.MinShortSide > c.MaxLongSide || c.AcceptedFormats == null)
            throw Fail("CONSTRAINTS", ErrorCode.InvalidArgument);
        var formats = c.AcceptedFormats.ToArray();
        if (formats.Length == 0 || formats.Any(f => !Enum.IsDefined(f))) throw Fail("FORMATS", ErrorCode.InvalidArgument);
        return c with { AcceptedFormats = formats.Distinct().ToArray() };
    }
    internal static void Validate(ImageData image)
    {
        if (image == null || image.Bytes == null || image.Bytes.Length == 0 || !Enum.IsDefined(image.Format))
            throw Fail("IMAGE", ErrorCode.ImageInvalid);
        PixelBytes(image.PixelWidth, image.PixelHeight);
        if (image.Bytes.LongLength > MaxEncoded) throw Fail("INPUT_BYTES", ErrorCode.ImageTooLarge);
    }
    internal static bool Fits(long bytes, ImageConstraints c) =>
        (c.MaxBytesAppliesToBase64 ? 4 * ((bytes + 2) / 3) : bytes) <= c.MaxEncodedBytes;
    internal static ImagePreparationLayout Layout(int width, int height, ImageConstraints c)
    {
        long longest = Math.Max(width, height), shortest = Math.Min(width, height);
        long numerator = shortest < c.MinShortSide ? c.MinShortSide : 1;
        long denominator = shortest < c.MinShortSide ? shortest : 1;
        if (numerator * longest > (long)c.MaxLongSide * denominator) { numerator = c.MaxLongSide; denominator = longest; }
        int w = checked((int)Math.Max(1, width * numerator / denominator));
        int h = checked((int)Math.Max(1, height * numerator / denominator));
        int cw = Math.Max(w, c.MinShortSide), ch = Math.Max(h, c.MinShortSide);
        PixelBytes(w, h); PixelBytes(cw, ch);
        return new(w, h, cw, ch);
    }
    internal static byte[] WhiteCanvas(byte[] premultiplied, ImagePreparationLayout layout, Action check)
    {
        if (premultiplied.Length != PixelBytes(layout.Width, layout.Height)) throw Fail("PIXEL_LENGTH", ErrorCode.ImageInvalid);
        var output = new byte[PixelBytes(layout.CanvasWidth, layout.CanvasHeight)];
        int left = (layout.CanvasWidth - layout.Width) / 2, top = (layout.CanvasHeight - layout.Height) / 2;
        for (int y = 0; y < layout.CanvasHeight; y++)
        {
            check();
            output.AsSpan(y * layout.CanvasWidth * 4, layout.CanvasWidth * 4).Fill(255);
            if (y < top || y >= top + layout.Height) continue;
            for (int x = 0; x < layout.Width; x++)
            {
                int source = ((y - top) * layout.Width + x) * 4, target = (y * layout.CanvasWidth + x + left) * 4;
                int white = 255 - premultiplied[source + 3];
                for (int channel = 0; channel < 3; channel++) output[target + channel] = (byte)Math.Min(255, premultiplied[source + channel] + white);
            }
        }
        return output;
    }
}
