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

        long workAreaLeft = workArea.X;
        long workAreaTop = workArea.Y;
        long workAreaRight = (long)workArea.X + workArea.Width;
        long workAreaBottom = (long)workArea.Y + workArea.Height;

        long x;
        long y;

        switch (mode)
        {
            case WindowPlacementMode.ScreenCenter:
                x = workAreaLeft + ((long)workArea.Width - finalWidth) / 2;
                y = workAreaTop + ((long)workArea.Height - finalHeight) / 2;
                break;

            case WindowPlacementMode.FollowCursor:
                var pt = anchor ?? fallbackCursor;
                long ptX = pt.X;
                long ptY = pt.Y;
                x = ptX + DefaultCursorOffsetPx;
                y = ptY + DefaultCursorOffsetPx;

                // 越界翻到光标左侧/上方
                if (x + finalWidth > workAreaRight)
                {
                    x = ptX - DefaultCursorOffsetPx - finalWidth;
                }

                if (y + finalHeight > workAreaBottom)
                {
                    y = ptY - DefaultCursorOffsetPx - finalHeight;
                }

                // 夹紧在工作区内
                x = Math.Clamp(x, workAreaLeft, Math.Max(workAreaLeft, workAreaRight - finalWidth));
                y = Math.Clamp(y, workAreaTop, Math.Max(workAreaTop, workAreaBottom - finalHeight));
                break;

            case WindowPlacementMode.RememberLast:
                if (lastBounds.HasValue && !lastBounds.Value.IsEmpty)
                {
                    x = lastBounds.Value.X;
                    y = lastBounds.Value.Y;
                    x = Math.Clamp(x, workAreaLeft, Math.Max(workAreaLeft, workAreaRight - finalWidth));
                    y = Math.Clamp(y, workAreaTop, Math.Max(workAreaTop, workAreaBottom - finalHeight));
                }
                else
                {
                    x = workAreaLeft + ((long)workArea.Width - finalWidth) / 2;
                    y = workAreaTop + ((long)workArea.Height - finalHeight) / 2;
                }
                break;

            default:
                x = workAreaLeft + ((long)workArea.Width - finalWidth) / 2;
                y = workAreaTop + ((long)workArea.Height - finalHeight) / 2;
                break;
        }

        return new PixelRect((int)x, (int)y, finalWidth, finalHeight);
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
            long clampedX = Math.Clamp((long)pt.X, (long)m.Bounds.X, (long)m.Bounds.Right - 1);
            long clampedY = Math.Clamp((long)pt.Y, (long)m.Bounds.Y, (long)m.Bounds.Bottom - 1);
            long dx = (long)pt.X - clampedX;
            long dy = (long)pt.Y - clampedY;
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

    public static bool ApplyWindowPos(nint hWnd, PixelRect bounds, bool focusInput)
    {
        return NativeWindowPlacementAdapter.Instance.SetWindowBounds(hWnd, bounds, show: true, activate: focusInput);
    }

    public static PixelRect? GetPhysicalWindowRect(nint hWnd)
    {
        if (NativeWindowPlacementAdapter.Instance.TryGetWindowBounds(hWnd, out var r))
        {
            return r;
        }
        return null;
    }
}
