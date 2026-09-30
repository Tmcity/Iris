using System;
using System.IO;
using System.Threading.Tasks;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage.Streams;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class Win32ClipboardServiceImageTests
{
    [Fact]
    public async Task SetImageAsync_WithSyntheticBmp_GeneratesValidDibV5_WithoutBmpFileHeader()
    {
        // 3x2 24bpp BMP: width 3, height 2
        byte[] bmpBytes = CreateSyntheticBmp(3, 2);

        var imageData = new ImageData(bmpBytes, ImageFormat.Bmp, 3, 2);
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetImageAsync(imageData, TestContext.Current.CancellationToken);

        adapter.ClipboardFormats.ContainsKey(DibV5Helper.CF_DIBV5).ShouldBeTrue();
        nint hMem = adapter.ClipboardFormats[DibV5Helper.CF_DIBV5];
        byte[] dibBytes = adapter.MemoryBlocks[hMem];

        // Verify total size = 124 header + 3 * 2 * 4 pixels = 148 bytes
        dibBytes.Length.ShouldBe(124 + 3 * 2 * 4);

        // Verify NOT containing 14-byte BMP file header (starts with 124, not 'BM' 0x42 0x4D)
        dibBytes[0].ShouldBe((byte)124);
        dibBytes[1].ShouldBe((byte)0);

        DibV5Helper.TryParseDibV5(dibBytes, out var header, out var pixels).ShouldBeTrue();
        header.bV5Size.ShouldBe(124u);
        header.bV5Width.ShouldBe(3);
        header.bV5Height.ShouldBe(-2, "Top-down DIB 必须使用负高度");
        header.bV5Planes.ShouldBe((ushort)1);
        header.bV5BitCount.ShouldBe((ushort)32);
        header.bV5Compression.ShouldBe(3u, "Compression 必须为 BI_BITFIELDS = 3");
        header.bV5SizeImage.ShouldBe((uint)(3 * 2 * 4));
        header.bV5RedMask.ShouldBe(0x00FF0000u);
        header.bV5GreenMask.ShouldBe(0x0000FF00u);
        header.bV5BlueMask.ShouldBe(0x000000FFu);
        header.bV5AlphaMask.ShouldBe(0xFF000000u);
        header.bV5CSType.ShouldBe(0x73524742u, "色彩空间必须为 sRGB");
        header.bV5Intent.ShouldBe(4u);

        pixels.Length.ShouldBe(3 * 2 * 4);
    }

    [Fact]
    public async Task SetImageAsync_WithSyntheticPngWithAlpha_PreservesStraightAlphaAndPixels()
    {
        // Create 2x2 PNG with transparent, semi-transparent, and opaque pixels
        // Pixel (0,0): Pure Red (B:0, G:0, R:255, A:255)
        // Pixel (1,0): Pure Green (B:0, G:255, R:0, A:255)
        // Pixel (0,1): Translucent Blue (B:255, G:0, R:0, A:128)
        // Pixel (1,1): Transparent (B:0, G:0, R:0, A:0)
        byte[] rawPixels = [
            0, 0, 255, 255,    // (0,0)
            0, 255, 0, 255,    // (1,0)
            255, 0, 0, 128,    // (0,1)
            0, 0, 0, 0         // (1,1)
        ];

        byte[] pngBytes = await EncodePixelsAsync(BitmapEncoder.PngEncoderId, 2, 2, rawPixels, BitmapAlphaMode.Straight);

        var imageData = new ImageData(pngBytes, ImageFormat.Png, 2, 2);
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetImageAsync(imageData, TestContext.Current.CancellationToken);

        nint hMem = adapter.ClipboardFormats[DibV5Helper.CF_DIBV5];
        byte[] dibBytes = adapter.MemoryBlocks[hMem];

        DibV5Helper.TryParseDibV5(dibBytes, out var header, out var pixels).ShouldBeTrue();
        header.bV5Width.ShouldBe(2);
        header.bV5Height.ShouldBe(-2);

        // Verify straight alpha pixels
        // Pixel 0: Red opaque
        pixels[0].ShouldBe((byte)0);
        pixels[1].ShouldBe((byte)0);
        pixels[2].ShouldBe((byte)255);
        pixels[3].ShouldBe((byte)255);

        // Pixel 2: Blue translucent (A=128)
        pixels[8].ShouldBe((byte)255);
        pixels[9].ShouldBe((byte)0);
        pixels[10].ShouldBe((byte)0);
        pixels[11].ShouldBe((byte)128);

        // Pixel 3: Transparent (A=0)
        pixels[15].ShouldBe((byte)0);
    }

    [Fact]
    public async Task SetImageAsync_WithSyntheticJpeg_HasOpaqueAlpha()
    {
        byte[] rawPixels = [
            10, 20, 30, 255,
            40, 50, 60, 255
        ];

        byte[] jpegBytes = await EncodePixelsAsync(BitmapEncoder.JpegEncoderId, 2, 1, rawPixels, BitmapAlphaMode.Ignore);

        var imageData = new ImageData(jpegBytes, ImageFormat.Jpeg, 2, 1);
        var adapter = new FakeNativeClipboardAdapter();
        var service = new Win32ClipboardService(null, null, adapter);

        await service.SetImageAsync(imageData, TestContext.Current.CancellationToken);

        nint hMem = adapter.ClipboardFormats[DibV5Helper.CF_DIBV5];
        byte[] dibBytes = adapter.MemoryBlocks[hMem];

        DibV5Helper.TryParseDibV5(dibBytes, out var header, out var pixels).ShouldBeTrue();
        header.bV5Width.ShouldBe(2);
        header.bV5Height.ShouldBe(-1);

        // JPEG has no alpha, decoded straight alpha must be 255 (opaque)
        pixels[3].ShouldBe((byte)255);
        pixels[7].ShouldBe((byte)255);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    public async Task SetImageAsync_NonPositiveDimensions_ThrowsImageInvalid(int width, int height)
    {
        var imageData = new ImageData([1, 2, 3], ImageFormat.Png, width, height);
        var service = new Win32ClipboardService();

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task SetImageAsync_NullOrEmptyBytes_ThrowsImageInvalid()
    {
        var service = new Win32ClipboardService();

        var ex1 = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(new ImageData(null!, ImageFormat.Png, 10, 10), TestContext.Current.CancellationToken));
        ex1.Code.ShouldBe(ErrorCode.ImageInvalid);

        var ex2 = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(new ImageData([], ImageFormat.Png, 10, 10), TestContext.Current.CancellationToken));
        ex2.Code.ShouldBe(ErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task SetImageAsync_FormatMismatch_ThrowsImageInvalid()
    {
        // Valid PNG bytes declared as Jpeg
        byte[] rawPixels = [255, 0, 0, 255];
        byte[] pngBytes = await EncodePixelsAsync(BitmapEncoder.PngEncoderId, 1, 1, rawPixels, BitmapAlphaMode.Straight);

        var imageData = new ImageData(pngBytes, ImageFormat.Jpeg, 1, 1);
        var service = new Win32ClipboardService();

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageInvalid);
        ex.Message.ShouldContain("codec");
    }

    [Fact]
    public async Task SetImageAsync_DimensionMismatch_ThrowsImageInvalid()
    {
        byte[] rawPixels = [255, 0, 0, 255];
        byte[] pngBytes = await EncodePixelsAsync(BitmapEncoder.PngEncoderId, 1, 1, rawPixels, BitmapAlphaMode.Straight);

        // Declared as 2x2 instead of 1x1
        var imageData = new ImageData(pngBytes, ImageFormat.Png, 2, 2);
        var service = new Win32ClipboardService();

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task SetImageAsync_CorruptedBytes_ThrowsImageInvalid()
    {
        byte[] corrupted = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFF]; // truncated PNG header
        var imageData = new ImageData(corrupted, ImageFormat.Png, 10, 10);
        var service = new Win32ClipboardService();

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageInvalid);
    }

    [Fact]
    public async Task SetImageAsync_EncodedBytesExceedBudget_ThrowsImageTooLarge()
    {
        // 33,554,432 + 1 bytes
        byte[] hugeBytes = new byte[33_554_433];
        var imageData = new ImageData(hugeBytes, ImageFormat.Png, 100, 100);
        var service = new Win32ClipboardService();

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageTooLarge);
    }

    [Theory]
    [InlineData(32_769, 10)]
    [InlineData(10, 32_769)]
    [InlineData(10_000, 4_000)] // 40,000,000 pixels > 33,554,432
    public async Task SetImageAsync_DimensionsOrTotalPixelsExceedBudget_ThrowsImageTooLarge(int width, int height)
    {
        var imageData = new ImageData([1, 2, 3], ImageFormat.Png, width, height);
        var service = new Win32ClipboardService();

        var ex = await Should.ThrowAsync<IrisException>(() =>
            service.SetImageAsync(imageData, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(ErrorCode.ImageTooLarge);
    }

    private static byte[] CreateSyntheticBmp(int width, int height)
    {
        int rowStride = checked((width * 3 + 3) & ~3);
        int pixelArraySize = checked(rowStride * height);
        int fileSize = 54 + pixelArraySize;

        byte[] bmp = new byte[fileSize];
        // BITMAPFILEHEADER
        bmp[0] = 0x42; // 'B'
        bmp[1] = 0x4D; // 'M'
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10); // offset

        // BITMAPINFOHEADER
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(0).CopyTo(bmp, 30); // BI_RGB
        BitConverter.GetBytes(pixelArraySize).CopyTo(bmp, 34);

        // Fill pixels
        for (int row = 0; row < height; row++)
        {
            int rowStart = 54 + row * rowStride;
            for (int col = 0; col < width; col++)
            {
                bmp[rowStart + col * 3] = (byte)(col * 50);     // B
                bmp[rowStart + col * 3 + 1] = (byte)(row * 80); // G
                bmp[rowStart + col * 3 + 2] = 200;              // R
            }
        }

        return bmp;
    }

    private static async Task<byte[]> EncodePixelsAsync(Guid codecId, uint width, uint height, byte[] bgraPixels, BitmapAlphaMode alphaMode)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(codecId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            alphaMode,
            width,
            height,
            96,
            96,
            bgraPixels);
        await encoder.FlushAsync();

        byte[] encoded = new byte[(int)stream.Size];
        var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(encoded);
        return encoded;
    }
}
