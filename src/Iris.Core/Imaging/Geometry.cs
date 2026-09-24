namespace Iris.Core.Imaging;

/// <summary>物理像素坐标（虚拟桌面坐标系，可为负数）。全系统跨层传递坐标只使用物理像素。</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>物理像素矩形（虚拟桌面坐标系）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
}
