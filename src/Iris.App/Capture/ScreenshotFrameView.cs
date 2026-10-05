using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.Core.Platform;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Capture;

namespace Iris.App.Capture;

internal sealed class ScreenshotFrameView
{
    internal CapturedFrame Frame { get; }
    internal BitmapSource[] Screens { get; }
    internal ScreenshotFrameView(CapturedFrame frame)
    {
        ScreenPixelLayout.ValidateFrameLayout(frame.Width, frame.Height, frame.Stride, frame.Bgra32Pixels.LongLength, frame.VirtualBounds);
        var topology = ScreenPixelLayout.Snapshot(frame.Monitors);
        if (topology.Bounds != frame.VirtualBounds) throw ScreenshotService.Failure("FRAME_TOPOLOGY");
        Frame = frame with { Monitors = topology.Monitors };
        var source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra32Pixels, frame.Stride);
        source.Freeze();
        Screens = Frame.Monitors.Select(m =>
        {
            var crop = new CroppedBitmap(source, new Int32Rect(checked((int)((long)m.Bounds.X - frame.VirtualBounds.X)),
                checked((int)((long)m.Bounds.Y - frame.VirtualBounds.Y)), m.Bounds.Width, m.Bounds.Height));
            crop.Freeze(); return (BitmapSource)crop;
        }).ToArray();
    }
    internal BitmapSource Magnify(int monitor, PixelPoint point)
    {
        var bounds = Frame.Monitors[monitor].Bounds;
        point = ScreenshotSelection.Clamp(point, bounds);
        byte[] pixels = new byte[15 * 15 * 4];
        for (int y = 0; y < 15; y++)
        for (int x = 0; x < 15; x++)
        {
            long px = Math.Clamp((long)point.X + x - 7, bounds.X, (long)bounds.X + bounds.Width - 1);
            long py = Math.Clamp((long)point.Y + y - 7, bounds.Y, (long)bounds.Y + bounds.Height - 1);
            int offset = checked((int)((py - Frame.VirtualBounds.Y) * Frame.Stride + (px - Frame.VirtualBounds.X) * 4));
            Frame.Bgra32Pixels.AsSpan(offset, 4).CopyTo(pixels.AsSpan((y * 15 + x) * 4, 4));
        }
        var bitmap = BitmapSource.Create(15, 15, 96, 96, PixelFormats.Bgra32, null, pixels, 60);
        bitmap.Freeze(); return bitmap;
    }
}
