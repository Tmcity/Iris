using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Wpf.Ui.Appearance;

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
    private static readonly SolidColorBrush HitTestBrush = CreateHitTestTransparentBrush();
    static SelectionIconWindow() => BackgroundProperty.OverrideMetadata(typeof(SelectionIconWindow),
        new FrameworkPropertyMetadata(HitTestBrush, null, static (d, _) =>
        {
            if (d is SelectionIconWindow window) window.EnsureTransparentCompositionTarget();
            return HitTestBrush;
        }));
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
        AllowsTransparency = true;
        ShowActivated = false; ShowInTaskbar = false; Focusable = false; Topmost = true;
        Left = Top = -10000;
        Title = UiText.MouseIconTranslate;
        Background = HitTestBrush;
        TranslateButton = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            Focusable = false,
            IsTabStop = false,
            ToolTip = UiText.MouseIconTranslate,
            Template = CreateButtonTemplate(HitTestBrush),
            Content = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Translate16,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        TranslateButton.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        AutomationProperties.SetName(TranslateButton, UiText.MouseIconTranslate);
        TranslateButton.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        TranslateButton.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        TranslateButton.Click += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        Content = TranslateButton;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            _native.Configure(handle);
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowMessage);
            EnsureTransparentCompositionTarget();
            ApplicationThemeManager.Changed += OnThemeChanged;
        };
        Closed += (_, _) => { _closed = true; ApplicationThemeManager.Changed -= OnThemeChanged; _source?.RemoveHook(WindowMessage); _source = null; };
    }
    private void OnThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        EnsureTransparentCompositionTarget();
        Dispatcher.BeginInvoke(EnsureTransparentCompositionTarget, System.Windows.Threading.DispatcherPriority.Send);
    }
    private void EnsureTransparentCompositionTarget()
    {
        if (_source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
    }
    private static SolidColorBrush CreateHitTestTransparentBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }
    private static ControlTemplate CreateButtonTemplate(Brush hitTestBrush)
    {
        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(FrameworkElement.WidthProperty, 24d);
        root.SetValue(FrameworkElement.HeightProperty, 24d);
        root.SetValue(Panel.BackgroundProperty, hitTestBrush);

        var border = new FrameworkElementFactory(typeof(Border), "CardBorder");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(UIElement.EffectProperty, null);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        border.AppendChild(presenter);
        root.AppendChild(border);
        template.VisualTree = root;

        var mouseOverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentFillColorDefaultBrush"), "CardBorder"));
        mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SolidBackgroundFillColorSecondaryBrush"), "CardBorder"));
        template.Triggers.Add(mouseOverTrigger);

        return template;
    }
    internal static nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE, never eat Click.
        return 0;
    }
    public void ShowAt(PixelRect bounds)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        Topmost = true;
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
