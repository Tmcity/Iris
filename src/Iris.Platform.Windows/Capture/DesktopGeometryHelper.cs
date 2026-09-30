using System;
using System.Collections.Generic;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;

namespace Iris.Platform.Windows.Capture;

internal static class DesktopGeometryHelper
{
    public static bool ValidateGeometry(
        int boundsLeft,
        int boundsTop,
        int boundsRight,
        int boundsBottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        double dpiScale)
    {
        long boundsWidth = (long)boundsRight - boundsLeft;
        long boundsHeight = (long)boundsBottom - boundsTop;
        long workWidth = (long)workRight - workLeft;
        long workHeight = (long)workBottom - workTop;

        if (boundsWidth <= 0 || boundsHeight <= 0 || workWidth <= 0 || workHeight <= 0)
        {
            return false;
        }

        if (boundsWidth > int.MaxValue || boundsHeight > int.MaxValue ||
            workWidth > int.MaxValue || workHeight > int.MaxValue)
        {
            return false;
        }

        long boundsRightEndpoint = (long)boundsLeft + boundsWidth;
        long boundsBottomEndpoint = (long)boundsTop + boundsHeight;
        if (boundsRightEndpoint > int.MaxValue || boundsRightEndpoint < int.MinValue ||
            boundsBottomEndpoint > int.MaxValue || boundsBottomEndpoint < int.MinValue)
        {
            return false;
        }

        long workRightEndpoint = (long)workLeft + workWidth;
        long workBottomEndpoint = (long)workTop + workHeight;
        if (workRightEndpoint > int.MaxValue || workRightEndpoint < int.MinValue ||
            workBottomEndpoint > int.MaxValue || workBottomEndpoint < int.MinValue)
        {
            return false;
        }

        if (workLeft < boundsLeft || workTop < boundsTop ||
            workRight > boundsRight || workBottom > boundsBottom)
        {
            return false;
        }

        if (double.IsNaN(dpiScale) || double.IsInfinity(dpiScale) || dpiScale <= 0)
        {
            return false;
        }

        return true;
    }

    public static MonitorInfo GetNearestMonitor(IReadOnlyList<MonitorInfo> monitors, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0)
        {
            throw new IrisException(ErrorCode.Unknown, "无法获取显示器信息");
        }

        MonitorInfo? best = null;
        Int128 bestDistSq = 0;

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            long right = (long)m.Bounds.X + m.Bounds.Width - 1;
            long bottom = (long)m.Bounds.Y + m.Bounds.Height - 1;

            long dx = 0;
            if (point.X < m.Bounds.X)
            {
                dx = (long)m.Bounds.X - point.X;
            }
            else if (point.X > right)
            {
                dx = (long)point.X - right;
            }

            long dy = 0;
            if (point.Y < m.Bounds.Y)
            {
                dy = (long)m.Bounds.Y - point.Y;
            }
            else if (point.Y > bottom)
            {
                dy = (long)point.Y - bottom;
            }

            Int128 distSq = (Int128)dx * (Int128)dx + (Int128)dy * (Int128)dy;

            if (best == null)
            {
                best = m;
                bestDistSq = distSq;
            }
            else if (distSq < bestDistSq)
            {
                best = m;
                bestDistSq = distSq;
            }
            else if (distSq == bestDistSq)
            {
                // Tie breaking: primary monitor first, then DeviceName Ordinal
                if (m.IsPrimary && !best.IsPrimary)
                {
                    best = m;
                    bestDistSq = distSq;
                }
                else if (m.IsPrimary == best.IsPrimary)
                {
                    if (string.Compare(m.DeviceName, best.DeviceName, StringComparison.Ordinal) < 0)
                    {
                        best = m;
                        bestDistSq = distSq;
                    }
                }
            }
        }

        return best!;
    }
}
