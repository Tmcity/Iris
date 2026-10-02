using System;
using System.Runtime.InteropServices;
using Iris.Core.Imaging;

namespace Iris.App.Translation;

/// <summary>
/// 窄 Win32 原生窗口定位适配器接口。
/// </summary>
internal interface INativeWindowPlacementAdapter
{
    bool SetWindowBounds(nint hWnd, PixelRect bounds, bool show, bool activate);
    bool TryGetWindowBounds(nint hWnd, out PixelRect bounds);
}

/// <summary>
/// 默认 Win32 原生窗口定位适配器。
/// </summary>
internal sealed class NativeWindowPlacementAdapter : INativeWindowPlacementAdapter
{
    public static NativeWindowPlacementAdapter Instance { get; } = new();

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    public bool SetWindowBounds(nint hWnd, PixelRect bounds, bool show, bool activate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        uint flags = SwpNoZOrder;
        if (!activate)
        {
            flags |= SwpNoActivate;
        }
        if (show)
        {
            flags |= SwpShowWindow;
        }

        return SetWindowPos(hWnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
    }

    public bool TryGetWindowBounds(nint hWnd, out PixelRect bounds)
    {
        bounds = default;
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hWnd, out var r))
        {
            return false;
        }

        bounds = new PixelRect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
        return true;
    }
}
