using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Iris.Core.Imaging;
using Iris.Core.Platform;

namespace Iris.App.Selection;

internal interface ISelectionIconView
{
    event EventHandler? Clicked;
    void ShowAt(PixelRect bounds);
    void HideIcon();
    void CloseIcon();
}

internal interface ISelectionIconViewFactory { ISelectionIconView Create(); }

internal sealed class SelectionIconViewFactory(SelectionIconNative native) : ISelectionIconViewFactory
{
    public ISelectionIconView Create() => new SelectionIconWindow(native);
}

internal sealed class SelectionIconWindow : Window, ISelectionIconView
{
    private readonly SelectionIconNative _native;
    private HwndSource? _source;
    private bool _closed;
    internal Button TranslateButton { get; }
    public event EventHandler? Clicked;
    internal SelectionIconWindow(SelectionIconNative native)
    {
        _native = native;
        Width = Height = 24;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Focusable = false;
        Left = Top = -10000;
        Title = UiText.MouseIconTranslate;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        TranslateButton = new Button
        {
            Content = global::Iris.App.Resources.AppResources.Get("Text_8a6cb14e2f9a"), Padding = new Thickness(0), BorderThickness = new Thickness(1), FontSize = 14,
            Focusable = false, IsTabStop = false, ToolTip = UiText.MouseIconTranslate,
        };
        TranslateButton.SetResourceReference(Control.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(TranslateButton, UiText.MouseIconTranslate);
        TranslateButton.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        TranslateButton.SetResourceReference(Control.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        TranslateButton.Click += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        Content = TranslateButton;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            _native.Configure(handle);
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowMessage);
        };
        Closed += (_, _) => { _closed = true; _source?.RemoveHook(WindowMessage); _source = null; };
    }
    internal static nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE, never eat Click.
        return 0;
    }
    public void ShowAt(PixelRect bounds)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        _native.Show(this, handle, bounds);
    }
    public void HideIcon() { if (!_closed) _native.Hide(this); }
    public void CloseIcon()
    {
        if (_closed) return;
        _native.Close(this);
        if (!_closed) throw new InvalidOperationException("Selection icon close incomplete.");
    }
}

internal static class SelectionIconGeometry
{
    internal static PixelRect Place(PixelPoint anchor, MonitorInfo monitor)
    {
        double scale = monitor.DpiScale;
        var work = monitor.WorkArea;
        if (!double.IsFinite(scale) || scale <= 0 || scale * 24 > int.MaxValue || work.IsEmpty) throw new InvalidOperationException("Invalid icon geometry.");
        int size = Math.Max(1, (int)Math.Round(24 * scale));
        long offset = checked((long)Math.Round(12 * scale));
        int width = Math.Min(size, work.Width), height = Math.Min(size, work.Height);
        long right = (long)work.X + work.Width, bottom = (long)work.Y + work.Height;
        long x = Math.Clamp((long)anchor.X + offset, work.X, right - width);
        long y = Math.Clamp((long)anchor.Y + offset, work.Y, bottom - height);
        return new(checked((int)x), checked((int)y), width, height);
    }
}
