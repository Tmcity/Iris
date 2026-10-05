using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Translation;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Settings;

namespace Iris.App.Capture;

internal sealed class ScreenshotInputEventArgs(ScreenshotInput input, PixelPoint point) : RoutedEventArgs(ScreenshotOverlayWindow.InputEvent)
{
    internal ScreenshotInput Input { get; } = input;
    internal PixelPoint Point { get; } = point;
}

internal sealed class ScreenshotOverlayWindow : Window
{
    internal static readonly RoutedEvent InputEvent = EventManager.RegisterRoutedEvent("ScreenshotInput", RoutingStrategy.Bubble,
        typeof(EventHandler<ScreenshotInputEventArgs>), typeof(ScreenshotOverlayWindow));
    internal event EventHandler<ScreenshotInputEventArgs> Input
    { add => AddHandler(InputEvent, value); remove => RemoveHandler(InputEvent, value); }
    internal event Action<Exception>? InputFailed;
    private readonly IScreenshotSystem _system;
    private readonly INativeWindowPlacementAdapter _placement;
    private readonly ScreenshotCanvas _canvas;
    private HwndSource? _source;
    private bool _closing, _releasing, _captureOwned;
    internal bool IsClosed { get; private set; }
    internal bool Established { get; set; }
    internal nint Handle { get; private set; }
    internal int MonitorIndex { get; }
    internal MonitorInfo Monitor { get; }
    internal BitmapSource? FrozenPixels => _canvas.FrozenPixels;

    internal ScreenshotOverlayWindow(IScreenshotSystem system, INativeWindowPlacementAdapter placement,
        ScreenshotFrameView frame, int monitor, ScreenshotSettings settings, bool dark)
    {
        _system = system; _placement = placement; MonitorIndex = monitor; Monitor = frame.Frame.Monitors[monitor];
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true;
        ShowInTaskbar = false; ShowActivated = false; WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Monitor.Bounds.Width / Monitor.DpiScale; Height = Monitor.Bounds.Height / Monitor.DpiScale;
        Left = Monitor.Bounds.X / Monitor.DpiScale; Top = Monitor.Bounds.Y / Monitor.DpiScale;
        Background = Brushes.Black;
        _canvas = new(frame, monitor, settings, dark); Content = _canvas;
        SourceInitialized += OnSourceInitialized; Closed += OnClosed;
        DpiChanged += OnDpiChanged; Deactivated += OnDeactivated; LostMouseCapture += OnLostCapture;
        PreviewMouseDown += OnDown; PreviewMouseMove += OnMove; PreviewMouseUp += OnUp; PreviewKeyDown += OnKey;
    }
    internal void ShowOwned(Action check)
    {
        check(); new WindowInteropHelper(this).EnsureHandle();
        check(); if (!_placement.SetWindowBounds(Handle, Monitor.Bounds, false, false)) throw ScreenshotService.Failure("POSITION");
        check(); _system.Show(this);
        check(); if (!_placement.SetWindowBounds(Handle, Monitor.Bounds, true, false)) throw ScreenshotService.Failure("SHOW_POSITION");
    }
    internal void CapturePointer()
    {
        _captureOwned = true;
        if (!_system.Capture(this)) throw ScreenshotService.Failure("MOUSE_CAPTURE");
    }
    internal void ReleasePointer()
    {
        if (!_captureOwned) return;
        _releasing = true;
        try { _system.ReleaseCapture(this); _captureOwned = false; }
        finally { _releasing = false; }
    }
    internal void CloseOwned()
    {
        _closing = true;
        ReleasePointer();
        if (!IsClosed) _system.Close(this);
        if (!IsClosed || Handle != 0) throw ScreenshotService.Failure("WINDOW_STILL_OPEN");
    }
    internal void UpdateSelection(PixelRect? selection, PixelPoint pointer) => _canvas.Update(selection, pointer);
    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        Handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(Handle); _source?.AddHook(Hook);
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (Established && message is 0x007E or 0x02E0) Route(ScreenshotInput.Topology, default);
        return 0;
    }
    private void OnDpiChanged(object sender, DpiChangedEventArgs args) { if (Established) Route(ScreenshotInput.Topology, default); }
    private void OnDeactivated(object? sender, EventArgs args) { if (Established) Route(ScreenshotInput.Deactivated, default); }
    private void OnLostCapture(object sender, MouseEventArgs args) { if (!_closing && !_releasing) Route(ScreenshotInput.LostCapture, default); }
    private void OnDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Right) Route(ScreenshotInput.Right, default);
        else if (args.ChangedButton == MouseButton.Left) RoutePointer(args.ClickCount >= 2 ? ScreenshotInput.DoubleClick : ScreenshotInput.Down, args);
        args.Handled = true;
    }
    private void OnMove(object sender, MouseEventArgs args) => RoutePointer(ScreenshotInput.Move, args);
    private void OnUp(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left) RoutePointer(ScreenshotInput.Up, args);
        args.Handled = true;
    }
    private void OnKey(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { Route(ScreenshotInput.Escape, default); args.Handled = true; }
        if (args.Key == Key.Enter) { Route(ScreenshotInput.Enter, default); args.Handled = true; }
    }
    private void RoutePointer(ScreenshotInput input, MouseEventArgs args)
    {
        try { Route(input, _system.PhysicalPoint(this, args.GetPosition(this))); }
        catch (Exception ex) { InputFailed?.Invoke(ex); }
    }
    private void Route(ScreenshotInput input, PixelPoint point)
    {
        if (!_closing) RaiseEvent(new ScreenshotInputEventArgs(input, point));
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        IsClosed = true; Handle = 0;
        _source?.RemoveHook(Hook); _source = null;
        SourceInitialized -= OnSourceInitialized; Closed -= OnClosed; DpiChanged -= OnDpiChanged;
        Deactivated -= OnDeactivated; LostMouseCapture -= OnLostCapture;
        PreviewMouseDown -= OnDown; PreviewMouseMove -= OnMove; PreviewMouseUp -= OnUp; PreviewKeyDown -= OnKey;
        _canvas.Release(); Content = null; DataContext = null;
    }
}

internal sealed class ScreenshotCanvas(ScreenshotFrameView frame, int monitor, ScreenshotSettings settings, bool dark) : FrameworkElement
{
    private ScreenshotFrameView? _frame = frame;
    private BitmapSource? _magnifier;
    private PixelRect? _selection;
    private PixelPoint _pointer = new(frame.Frame.Monitors[monitor].Bounds.X, frame.Frame.Monitors[monitor].Bounds.Y);
    internal BitmapSource? FrozenPixels => _frame?.Screens[monitor];
    internal void Release() { _frame = null; _magnifier = null; }
    internal void Update(PixelRect? selection, PixelPoint point)
    {
        _selection = selection; _pointer = point;
        _magnifier = settings.ShowMagnifier ? _frame?.Magnify(monitor, point) : null;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_frame == null) return;
        var m = _frame.Frame.Monitors[monitor]; var b = m.Bounds; double scale = m.DpiScale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.PushTransform(new ScaleTransform(1 / scale, 1 / scale));
        dc.DrawImage(_frame.Screens[monitor], new Rect(0, 0, b.Width, b.Height));
        var mask = new SolidColorBrush(Color.FromArgb((byte)Math.Round(settings.MaskOpacity * 255), 0, 0, 0));
        Rect selected = _selection is { } r ? new((double)r.X - b.X, (double)r.Y - b.Y, r.Width, r.Height) : Rect.Empty;
        if (selected.IsEmpty) dc.DrawRectangle(mask, null, new Rect(0, 0, b.Width, b.Height));
        else
        {
            dc.DrawRectangle(mask, null, new Rect(0, 0, b.Width, selected.Top));
            dc.DrawRectangle(mask, null, new Rect(0, selected.Bottom, b.Width, Math.Max(0, b.Height - selected.Bottom)));
            dc.DrawRectangle(mask, null, new Rect(0, selected.Top, selected.Left, selected.Height));
            dc.DrawRectangle(mask, null, new Rect(selected.Right, selected.Top, Math.Max(0, b.Width - selected.Right), selected.Height));
            dc.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 1), selected);
        }
        var foreground = dark ? Brushes.White : Brushes.Black;
        var chrome = dark ? Brushes.Black : Brushes.White;
        Rect magnifierBounds = Rect.Empty;
        double magnifierHeight = 124 + Math.Ceiling(24 * scale);
        if (settings.ShowMagnifier && _magnifier != null && b.Width >= 124 && b.Height >= magnifierHeight)
        {
            double mx = (long)_pointer.X - b.X + 20, my = (long)_pointer.Y - b.Y + 24;
            if (mx + 124 > b.Width) mx = (long)_pointer.X - b.X - 144;
            if (my + magnifierHeight > b.Height) my = (long)_pointer.Y - b.Y - magnifierHeight - 24;
            magnifierBounds = new Rect(Math.Clamp(mx, 0, b.Width - 124), Math.Clamp(my, 0, b.Height - magnifierHeight), 124, magnifierHeight);
        }
        void Label(string text, double x, double y, bool center = false, bool avoidMagnifier = false)
        {
            var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12 * scale, foreground, scale);
            double width = Math.Min(b.Width, label.Width + 12 * scale), height = Math.Min(b.Height, label.Height + 8 * scale);
            if (center) x = (b.Width - width) / 2;
            x = Math.Clamp(x, 0, Math.Max(0, b.Width - width)); y = Math.Clamp(y, 0, Math.Max(0, b.Height - height));
            if (avoidMagnifier && !magnifierBounds.IsEmpty && magnifierBounds.IntersectsWith(new Rect(x, y, width, height)))
            {
                y = magnifierBounds.Top >= height + 6 * scale ? magnifierBounds.Top - height - 6 * scale : magnifierBounds.Bottom + 6 * scale;
                y = Math.Clamp(y, 0, Math.Max(0, b.Height - height));
                if (magnifierBounds.IntersectsWith(new Rect(x, y, width, height))) return;
            }
            dc.DrawRectangle(chrome, null, new Rect(x, y, width, height)); dc.DrawText(label, new Point(x + 6 * scale, y + 4 * scale));
        }
        if (b.Width > 260 * scale && b.Height > 90 * scale)
            Label(settings.ConfirmOnMouseUp || !ScreenshotSelection.Valid(_selection) ? UiText.ScreenshotDragHint : UiText.ScreenshotConfirmHint, 12 * scale, 12 * scale, center: true, avoidMagnifier: true);
        if (_selection is { } rect && b.Width > 100 * scale && b.Height > 40 * scale)
            Label($"{rect.Width} × {rect.Height}", selected.Left, selected.Bottom + 6 * scale, avoidMagnifier: true);
        if (!magnifierBounds.IsEmpty)
        {
            double x = magnifierBounds.X, y = magnifierBounds.Y;
            dc.DrawRectangle(chrome, null, new Rect(x, y, 124, 124));
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(_magnifier, new Rect(x + 2, y + 2, 120, 120));
            dc.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 1), new Rect(x + 58, y + 58, 8, 8));
            Label($"{_pointer.X}, {_pointer.Y}", x, y + 124);
        }
        dc.Pop(); dc.Pop();
    }
}
