using Iris.Core.Platform;

namespace Iris.Platform.Windows.Hooks;

/// <summary>纯值状态机；外部 worker 负责来源、时效及丢包确认。</summary>
internal sealed class GestureRecognizer(MouseSelectionMonitorOptions options)
{
    private MouseSample? _down;
    private MouseSample? _previousClick;
    private uint? _lastTime;
    private long _generation;
    private int _clicks;
    private bool _dragged;

    internal void Reset()
    {
        _down = _previousClick = null;
        _lastTime = null;
        _clicks = 0;
        _dragged = false;
    }

    internal SelectionGesture? Accept(MouseSample sample, long generation, ClickMetrics metrics)
    {
        if (_generation != generation) Reset();
        _generation = generation;
        if (_lastTime is uint last && unchecked(sample.Time - last) > int.MaxValue)
        {
            Reset();
            return null;
        }
        _lastTime = sample.Time;
        if (sample.Injected || sample.Message is MouseMessage.Wheel or MouseMessage.HorizontalWheel ||
            (sample.IsDown && sample.Message != MouseMessage.LeftDown))
        {
            Reset();
            return null;
        }
        if (sample.Message == MouseMessage.LeftDown)
        {
            if (_down != null || (options.RequireIBeamCursor && !sample.IBeam) || sample.Source == default)
            {
                Reset();
                return null;
            }
            if (_previousClick is { } previous && previous.Source != sample.Source) Reset();
            _down = sample;
            _dragged = false;
            return null;
        }
        if (_down is not { } down) return null;
        if (sample.Message == MouseMessage.Move || sample.Message == MouseMessage.LeftUp)
        {
            long dx = (long)sample.Point.X - down.Point.X;
            long dy = (long)sample.Point.Y - down.Point.Y;
            _dragged |= (Int128)dx * dx + (Int128)dy * dy >= (long)options.MinDragDistancePx * options.MinDragDistancePx;
        }
        if (sample.Message != MouseMessage.LeftUp) return null;
        if (sample.Source != down.Source) { Reset(); return null; }
        _down = null;
        if (_dragged || down.Shift)
        {
            var gesture = _dragged ? SelectionGesture.Drag : SelectionGesture.ShiftClick;
            Reset();
            return gesture;
        }
        bool adjacent = _previousClick is { } prior && prior.Source == down.Source &&
            unchecked(down.Time - prior.Time) <= metrics.Time && metrics.Width > 0 && metrics.Height > 0 &&
            InRectangle(down, prior, metrics);
        _clicks = adjacent && _clicks < 3 ? _clicks + 1 : 1;
        _previousClick = down;
        return _clicks switch { 2 => SelectionGesture.DoubleClick, 3 => SelectionGesture.TripleClick, _ => null };
    }

    private static bool InRectangle(MouseSample current, MouseSample previous, ClickMetrics metrics)
    {
        long left = (long)previous.Point.X - metrics.Width / 2;
        long top = (long)previous.Point.Y - metrics.Height / 2;
        return current.Point.X >= left && current.Point.X < left + metrics.Width &&
            current.Point.Y >= top && current.Point.Y < top + metrics.Height;
    }
}
