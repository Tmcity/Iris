using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Settings;

namespace Iris.App.Translation;

/// <summary>
/// 纯几何定位计算与 Win32 原生 SetWindowPos 适配器。
/// 隔离 IDesktopContext 快照与 HWND 调用；完整规则见 TRANSLATION_WINDOW.md §7 与 §9。
/// </summary>
internal static partial class WindowPlacementHelper
{
    public const int DefaultCursorOffsetPx = 16;

    public static MonitorInfo? FindTargetMonitor(
        WindowPlacementMode mode,
        PixelPoint? anchor,
        PixelRect? lastBounds,
        IReadOnlyList<MonitorInfo> monitors,
        PixelPoint fallbackCursor)
    {
        if (monitors == null || monitors.Count == 0)
        {
            return null;
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        if (mode == WindowPlacementMode.RememberLast && lastBounds.HasValue && !lastBounds.Value.IsEmpty)
        {
            var bounds = lastBounds.Value;
            var center = new PixelPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var containingMonitor = monitors.FirstOrDefault(m => m.Bounds.Contains(center))
                ?? monitors.FirstOrDefault(m => Intersects(m.Bounds, bounds));

            if (containingMonitor != null)
            {
                return containingMonitor;
            }

            // 拔屏回退主屏
            return primary;
        }

        var targetPoint = anchor ?? fallbackCursor;
        var matched = monitors.FirstOrDefault(m => m.Bounds.Contains(targetPoint));
        if (matched != null)
        {
            return matched;
        }

        // 最近显示器算法
        return FindNearestMonitor(targetPoint, monitors) ?? primary;
    }

    public static PixelRect CalculateBounds(
        WindowPlacementMode mode,
        PixelPoint? anchor,
        PixelRect? lastBounds,
        MonitorInfo targetMonitor,
        int desiredWidthPx,
        int desiredHeightPx,
        PixelPoint fallbackCursor)
    {
        ArgumentNullException.ThrowIfNull(targetMonitor);

        var workArea = targetMonitor.WorkArea;
        // 工作区小于窗口时允许缩至工作区
        int finalWidth = Math.Max(1, Math.Min(desiredWidthPx, workArea.Width));
        int finalHeight = Math.Max(1, Math.Min(desiredHeightPx, workArea.Height));

        int x;
        int y;

        switch (mode)
        {
            case WindowPlacementMode.ScreenCenter:
                x = workArea.X + (workArea.Width - finalWidth) / 2;
                y = workArea.Y + (workArea.Height - finalHeight) / 2;
                break;

            case WindowPlacementMode.FollowCursor:
                var pt = anchor ?? fallbackCursor;
                x = pt.X + DefaultCursorOffsetPx;
                y = pt.Y + DefaultCursorOffsetPx;

                // 越界翻到光标左侧/上方
                if (x + finalWidth > workArea.Right)
                {
                    x = pt.X - DefaultCursorOffsetPx - finalWidth;
                }

                if (y + finalHeight > workArea.Bottom)
                {
                    y = pt.Y - DefaultCursorOffsetPx - finalHeight;
                }

                // 夹紧在工作区内
                x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - finalWidth));
                y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - finalHeight));
                break;

            case WindowPlacementMode.RememberLast:
                if (lastBounds.HasValue && !lastBounds.Value.IsEmpty)
                {
                    x = lastBounds.Value.X;
                    y = lastBounds.Value.Y;
                    x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - finalWidth));
                    y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - finalHeight));
                }
                else
                {
                    x = workArea.X + (workArea.Width - finalWidth) / 2;
                    y = workArea.Y + (workArea.Height - finalHeight) / 2;
                }
                break;

            default:
                x = workArea.X + (workArea.Width - finalWidth) / 2;
                y = workArea.Y + (workArea.Height - finalHeight) / 2;
                break;
        }

        return new PixelRect(x, y, finalWidth, finalHeight);
    }

    private static bool Intersects(PixelRect a, PixelRect b)
    {
        return a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
    }

    private static MonitorInfo? FindNearestMonitor(PixelPoint pt, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        long bestDistSq = long.MaxValue;

        foreach (var m in monitors)
        {
            int clampedX = Math.Clamp(pt.X, m.Bounds.X, m.Bounds.Right - 1);
            int clampedY = Math.Clamp(pt.Y, m.Bounds.Y, m.Bounds.Bottom - 1);
            long dx = pt.X - clampedX;
            long dy = pt.Y - clampedY;
            long distSq = dx * dx + dy * dy;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = m;
            }
            else if (distSq == bestDistSq && m.IsPrimary)
            {
                best = m;
            }
        }

        return best;
    }

    // Win32 常量与 P/Invoke
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

    public static bool ApplyWindowPos(nint hWnd, PixelRect bounds, bool focusInput)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        uint flags = SwpNoZOrder | SwpShowWindow;
        if (!focusInput)
        {
            flags |= SwpNoActivate;
        }

        return SetWindowPos(hWnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
    }

    public static PixelRect? GetPhysicalWindowRect(nint hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        if (GetWindowRect(hWnd, out var r))
        {
            return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        return null;
    }
}
