using Iris.Core.Imaging;

namespace Iris.App.Capture;

internal enum ScreenshotInput { Down, Move, Up, DoubleClick, Enter, Right, Escape, LostCapture, Deactivated, Topology }

/// <summary>Discrete physical pixels; exclusive right/bottom are calculated in long.</summary>
internal sealed class ScreenshotSelection
{
    internal PixelRect? Region { get; private set; }
    internal int Monitor { get; private set; } = -1;
    internal bool Dragging { get; private set; }
    private PixelPoint _start;
    private PixelRect? _moving;
    internal static bool Contains(PixelRect r, PixelPoint p) => p.X >= r.X && p.Y >= r.Y &&
        (long)p.X < (long)r.X + r.Width && (long)p.Y < (long)r.Y + r.Height;
    internal static bool Valid(PixelRect? r) => r is { Width: >= 4, Height: >= 4 };
    internal static PixelPoint Clamp(PixelPoint p, PixelRect b) => new(
        checked((int)Math.Clamp((long)p.X, b.X, (long)b.X + b.Width - 1)),
        checked((int)Math.Clamp((long)p.Y, b.Y, (long)b.Y + b.Height - 1)));
    internal void Down(int monitor, PixelRect bounds, PixelPoint point, bool allowMove)
    {
        point = Clamp(point, bounds);
        _moving = allowMove && Monitor == monitor && Region is { } r && Contains(r, point) ? r : null;
        Monitor = monitor; _start = point; Dragging = true;
        if (_moving == null) Region = new(point.X, point.Y, 1, 1);
    }
    internal void Move(PixelRect bounds, PixelPoint point)
    {
        if (!Dragging) return;
        point = Clamp(point, bounds);
        if (_moving is { } old)
        {
            long x = Math.Clamp((long)old.X + point.X - _start.X, bounds.X, (long)bounds.X + bounds.Width - old.Width);
            long y = Math.Clamp((long)old.Y + point.Y - _start.Y, bounds.Y, (long)bounds.Y + bounds.Height - old.Height);
            Region = new(checked((int)x), checked((int)y), old.Width, old.Height);
        }
        else
        {
            long x = Math.Min(_start.X, point.X), y = Math.Min(_start.Y, point.Y);
            Region = new(checked((int)x), checked((int)y), checked((int)(Math.Max(_start.X, point.X) - x + 1)),
                checked((int)(Math.Max(_start.Y, point.Y) - y + 1)));
        }
    }
    internal void Up(PixelRect bounds, PixelPoint point)
    {
        Move(bounds, point); Dragging = false; _moving = null;
        if (!Valid(Region)) Region = null;
    }
    internal void Clear() { Region = null; Monitor = -1; Dragging = false; _moving = null; }
}
