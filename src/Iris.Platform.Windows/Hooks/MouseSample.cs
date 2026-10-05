using Iris.Core.Imaging;

namespace Iris.Platform.Windows.Hooks;

internal enum MouseMessage : uint
{
    Move = 0x200, LeftDown = 0x201, LeftUp = 0x202,
    RightDown = 0x204, RightUp = 0x205, MiddleDown = 0x207, MiddleUp = 0x208,
    Wheel = 0x20A, XDown = 0x20B, XUp = 0x20C, HorizontalWheel = 0x20E,
}

internal readonly record struct MouseSource(nint Window, uint ProcessId);
internal readonly record struct ClickMetrics(uint Time, int Width, int Height);
internal readonly record struct MouseSample(MouseMessage Message, PixelPoint Point, uint Time, uint Flags,
    MouseSource Source = default, bool IBeam = false, bool Shift = false)
{
    internal bool IsDown => Message is MouseMessage.LeftDown or MouseMessage.RightDown or MouseMessage.MiddleDown or MouseMessage.XDown;
    internal bool IsUp => Message is MouseMessage.LeftUp or MouseMessage.RightUp or MouseMessage.MiddleUp or MouseMessage.XUp;
    internal bool Injected => (Flags & 3) != 0;
}

internal readonly record struct QueuedMouseSample(MouseSample Sample, long Epoch);
