using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iris.Platform.Windows.Clipboard;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BITMAPV5HEADER
{
    public const uint HeaderSize = 124;
    public const uint BiBitfields = 3;
    public const uint LcsSRgb = 0x73524742; // 'sRGB'
    public const uint LcsGmImages = 4;

    public uint bV5Size;
    public int bV5Width;
    public int bV5Height;
    public ushort bV5Planes;
    public ushort bV5BitCount;
    public uint bV5Compression;
    public uint bV5SizeImage;
    public int bV5XPelsPerMeter;
    public int bV5YPelsPerMeter;
    public uint bV5ClrUsed;
    public uint bV5ClrImportant;
    public uint bV5RedMask;
    public uint bV5GreenMask;
    public uint bV5BlueMask;
    public uint bV5AlphaMask;
    public uint bV5CSType;
    public uint bV5Endpoints_ciexyzRed_X;
    public uint bV5Endpoints_ciexyzRed_Y;
    public uint bV5Endpoints_ciexyzRed_Z;
    public uint bV5Endpoints_ciexyzGreen_X;
    public uint bV5Endpoints_ciexyzGreen_Y;
    public uint bV5Endpoints_ciexyzGreen_Z;
    public uint bV5Endpoints_ciexyzBlue_X;
    public uint bV5Endpoints_ciexyzBlue_Y;
    public uint bV5Endpoints_ciexyzBlue_Z;
    public uint bV5GammaRed;
    public uint bV5GammaGreen;
    public uint bV5GammaBlue;
    public uint bV5Intent;
    public uint bV5ProfileData;
    public uint bV5ProfileSize;
    public uint bV5Reserved;
}

internal static class DibV5Helper
{
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_DIBV5 = 17;
    public const uint GMEM_MOVEABLE = 0x0002;

    public static byte[] CreateDibV5(int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int imageSize = checked(width * height * 4);
        if (bgraPixels.Length != imageSize)
        {
            throw new ArgumentException($"Pixel buffer size {bgraPixels.Length} does not match expected {imageSize}.", nameof(bgraPixels));
        }

        var header = new BITMAPV5HEADER
        {
            bV5Size = BITMAPV5HEADER.HeaderSize,
            bV5Width = width,
            bV5Height = -height, // Negative for top-down DIB
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BITMAPV5HEADER.BiBitfields,
            bV5SizeImage = (uint)imageSize,
            bV5XPelsPerMeter = 0,
            bV5YPelsPerMeter = 0,
            bV5ClrUsed = 0,
            bV5ClrImportant = 0,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5CSType = BITMAPV5HEADER.LcsSRgb,
            bV5Intent = BITMAPV5HEADER.LcsGmImages
        };

        byte[] result = new byte[BITMAPV5HEADER.HeaderSize + imageSize];
        Unsafe.WriteUnaligned(ref result[0], header);
        bgraPixels.CopyTo(result.AsSpan((int)BITMAPV5HEADER.HeaderSize));

        return result;
    }

    public static bool TryParseDibV5(ReadOnlySpan<byte> dibV5Bytes, out BITMAPV5HEADER header, out ReadOnlySpan<byte> pixels)
    {
        header = default;
        pixels = default;

        if (dibV5Bytes.Length < (int)BITMAPV5HEADER.HeaderSize)
        {
            return false;
        }

        header = Unsafe.ReadUnaligned<BITMAPV5HEADER>(ref MemoryMarshal.GetReference(dibV5Bytes));
        if (header.bV5Size != BITMAPV5HEADER.HeaderSize)
        {
            return false;
        }

        pixels = dibV5Bytes.Slice((int)BITMAPV5HEADER.HeaderSize);
        return true;
    }
}
