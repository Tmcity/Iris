using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;

namespace Iris.Platform.Windows.Capture;

internal static class ScreenPixelLayout
{
    internal const int MaxDimension = 32768;
    internal const long MaxPixels = 67_108_864;
    internal const int MaxRawBytes = 268_435_456;
    internal const int MaxEncodedBytes = 285_212_672;
    internal static int EncodedLength(ulong size)
    {
        if (size > MaxEncodedBytes) throw ScreenCaptureErrors.Fail("ENCODED_BUDGET", ErrorCode.ImageTooLarge);
        if (size == 0) throw ScreenCaptureErrors.Fail("EMPTY_ENCODING");
        return checked((int)size);
    }

    internal static int ValidateSize(long width, long height, ErrorCode invalid)
    {
        if (width <= 0 || height <= 0) throw ScreenCaptureErrors.Fail("DIMENSIONS", invalid);
        if (width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
            throw ScreenCaptureErrors.Fail("PIXEL_BUDGET", ErrorCode.ImageTooLarge);
        return checked((int)(width * height * 4));
    }
    internal static void ValidateFrameLayout(int width, int height, int stride, long bufferLength, PixelRect bounds)
    {
        ValidateSize(width, height, ErrorCode.ImageInvalid);
        if (bounds.Width != width || bounds.Height != height || stride < (long)width * 4)
            throw ScreenCaptureErrors.Fail("FRAME_LAYOUT", ErrorCode.ImageInvalid);
        long footprint = checked((long)(height - 1) * stride + (long)width * 4);
        if (footprint > MaxRawBytes || bufferLength > MaxRawBytes) throw ScreenCaptureErrors.Fail("RAW_BUDGET", ErrorCode.ImageTooLarge);
        if (bufferLength < footprint) throw ScreenCaptureErrors.Fail("FRAME_BUFFER", ErrorCode.ImageInvalid);
    }
    internal static PixelRect Intersection(CapturedFrame frame, PixelRect region, ImageFormat format)
    {
        if (frame?.Bgra32Pixels == null || !Enum.IsDefined(format)) throw ScreenCaptureErrors.Fail("FRAME_FORMAT", ErrorCode.ImageInvalid);
        ValidateFrameLayout(frame.Width, frame.Height, frame.Stride, frame.Bgra32Pixels.LongLength, frame.VirtualBounds);
        if (region.Width <= 0 || region.Height <= 0) throw ScreenCaptureErrors.Fail("REGION");
        long left = Math.Max((long)region.X, frame.VirtualBounds.X), top = Math.Max((long)region.Y, frame.VirtualBounds.Y);
        long right = Math.Min((long)region.X + region.Width, (long)frame.VirtualBounds.X + frame.Width);
        long bottom = Math.Min((long)region.Y + region.Height, (long)frame.VirtualBounds.Y + frame.Height);
        ValidateSize(right - left, bottom - top, ErrorCode.CaptureFailed);
        // Crop only needs local coordinates; the absolute exclusive endpoints can exceed int.MaxValue.
        return new(checked((int)(left - frame.VirtualBounds.X)), checked((int)(top - frame.VirtualBounds.Y)), checked((int)(right - left)), checked((int)(bottom - top)));
    }
    internal static byte[] CopyCrop(CapturedFrame frame, PixelRect local, Action check)
    {
        check();
        int stride = checked(local.Width * 4);
        var pixels = new byte[ValidateSize(local.Width, local.Height, ErrorCode.ImageInvalid)];
        for (int y = 0; y < local.Height; y++)
        {
            check();
            int source = checked((int)(((long)local.Y + y) * frame.Stride + (long)local.X * 4));
            frame.Bgra32Pixels.AsSpan(source, stride).CopyTo(pixels.AsSpan(y * stride, stride));
            for (int x = 3; x < stride; x += 4) pixels[y * stride + x] = 255;
        }
        check(); return pixels;
    }
    internal static (IReadOnlyList<MonitorInfo> Monitors, PixelRect Bounds) Snapshot(IReadOnlyList<MonitorInfo>? monitors)
    {
        if (monitors == null || monitors.Count == 0) throw ScreenCaptureErrors.Fail("MONITORS");
        var copy = new MonitorInfo[monitors.Count];
        long left = long.MaxValue, top = long.MaxValue, right = long.MinValue, bottom = long.MinValue;
        for (int i = 0; i < copy.Length; i++)
        {
            var m = monitors[i];
            if (m == null || string.IsNullOrWhiteSpace(m.DeviceName) || m.Bounds.Width <= 0 || m.Bounds.Height <= 0 ||
                m.WorkArea.Width <= 0 || m.WorkArea.Height <= 0 || !double.IsFinite(m.DpiScale) || m.DpiScale <= 0)
                throw ScreenCaptureErrors.Fail("MONITOR_GEOMETRY");
            long r = (long)m.Bounds.X + m.Bounds.Width, b = (long)m.Bounds.Y + m.Bounds.Height;
            if (m.WorkArea.X < m.Bounds.X || m.WorkArea.Y < m.Bounds.Y || (long)m.WorkArea.X + m.WorkArea.Width > r || (long)m.WorkArea.Y + m.WorkArea.Height > b)
                throw ScreenCaptureErrors.Fail("WORK_AREA");
            copy[i] = m with { };
            left = Math.Min(left, m.Bounds.X); top = Math.Min(top, m.Bounds.Y); right = Math.Max(right, r); bottom = Math.Max(bottom, b);
        }
        ValidateSize(right - left, bottom - top, ErrorCode.CaptureFailed);
        return (Array.AsReadOnly(copy), new(checked((int)left), checked((int)top), checked((int)(right - left)), checked((int)(bottom - top))));
    }
    internal static bool SameTopology(IReadOnlyList<MonitorInfo> a, IReadOnlyList<MonitorInfo> b)
    {
        if (a.Count != b.Count) return false;
        var counts = a.GroupBy(m => m).ToDictionary(group => group.Key, group => group.Count());
        foreach (var monitor in b)
        {
            if (!counts.TryGetValue(monitor, out int count) || count == 0) return false;
            counts[monitor] = count - 1;
        }
        return true;
    }
}
