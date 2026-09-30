using System;
using System.Threading;
using System.Threading.Tasks;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage.Streams;
using Iris.Core.Errors;
using Iris.Core.Imaging;

namespace Iris.Platform.Windows.Clipboard;

internal static class WindowsImageDecoder
{
    public const int MaxEncodedBytes = 33_554_432; // 32 MB
    public const int MaxDimension = 32_768;
    public const int MaxPixels = 33_554_432;

    public static async Task<byte[]> DecodeToDibV5Async(ImageData image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (image == null || image.Bytes == null || image.Bytes.Length == 0)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Image data or bytes cannot be null or empty.");
        }

        if (!Enum.IsDefined(image.Format))
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Unsupported or undefined image format.");
        }

        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Image dimensions must be positive.");
        }

        // Budget check 1: Encoded bytes and requested dimensions
        if (image.Bytes.Length > MaxEncodedBytes)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Image encoded bytes exceed maximum limit.");
        }

        if (image.PixelWidth > MaxDimension || image.PixelHeight > MaxDimension)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Image dimensions exceed maximum limit.");
        }

        long requestedPixels = checked((long)image.PixelWidth * image.PixelHeight);
        if (requestedPixels > MaxPixels)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Total image pixels exceed maximum limit.");
        }

        using var stream = new InMemoryRandomAccessStream();
        try
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(image.Bytes);
                await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
                writer.DetachStream();
            }
            stream.Seek(0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Failed to write image data to stream.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Failed to decode image data.");
        }

        Guid expectedCodecId = image.Format switch
        {
            ImageFormat.Png => BitmapDecoder.PngDecoderId,
            ImageFormat.Jpeg => BitmapDecoder.JpegDecoderId,
            ImageFormat.Bmp => BitmapDecoder.BmpDecoderId,
            _ => Guid.Empty
        };

        if (decoder.DecoderInformation.CodecId != expectedCodecId)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Image format does not match actual encoded codec.");
        }

        uint actualWidth = decoder.PixelWidth;
        uint actualHeight = decoder.PixelHeight;

        // Budget check 2: Decoded dimensions from header
        if (actualWidth > (uint)MaxDimension || actualHeight > (uint)MaxDimension)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Decoded image dimensions exceed maximum limit.");
        }

        long actualPixels = checked((long)actualWidth * actualHeight);
        if (actualPixels > MaxPixels)
        {
            throw new IrisException(ErrorCode.ImageTooLarge, "Decoded total pixels exceed maximum limit.");
        }

        if (actualWidth != (uint)image.PixelWidth || actualHeight != (uint)image.PixelHeight)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Image declared dimensions do not match actual decoded dimensions.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Explicitly ignore EXIF orientation per specification; enforce raw dimensions and straight BGRA8
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb
            ).AsTask(cancellationToken).ConfigureAwait(false);

            if (softwareBitmap.PixelWidth != (int)actualWidth || softwareBitmap.PixelHeight != (int)actualHeight)
            {
                throw new IrisException(ErrorCode.ImageInvalid, "Decoded software bitmap dimensions do not match raw decoder dimensions.");
            }

            int imageByteSize = checked((int)(actualWidth * actualHeight * 4));
            var pixelBuffer = new global::Windows.Storage.Streams.Buffer((uint)imageByteSize);
            softwareBitmap.CopyToBuffer(pixelBuffer);

            using var dataReader = DataReader.FromBuffer(pixelBuffer);
            byte[] pixelBytes = new byte[pixelBuffer.Length];
            dataReader.ReadBytes(pixelBytes);

            if (pixelBytes.Length != imageByteSize)
            {
                throw new IrisException(ErrorCode.ImageInvalid, "Decoded pixel buffer byte length mismatch.");
            }

            return DibV5Helper.CreateDibV5((int)actualWidth, (int)actualHeight, pixelBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IrisException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IrisException(ErrorCode.ImageInvalid, "Failed to decode image pixels.");
        }
    }
}
