using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Iris.App.Translation;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Wpf.Ui.Controls;

namespace Iris.App.History;

internal sealed partial class HistoryWindow : FluentWindow
{
    internal const double WideLayoutBreakpoint = 640d;

    private readonly IDesktopContext _desktop;
    private readonly INativeWindowPlacementAdapter _placement;

    internal bool ForceClose { get; set; }
    internal bool IsWideLayout { get; private set; } = true;
    private bool _layoutApplied;

    public HistoryWindow(IDesktopContext desktop, INativeWindowPlacementAdapter placement)
    {
        _desktop = desktop;
        _placement = placement;
        InitializeComponent();
        SourceInitialized += (_, _) => PlaceInWorkArea();
        DpiChanged += (_, _) => PlaceInWorkArea();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void PlaceInWorkArea()
    {
        var monitors = _desktop.GetMonitors();
        var cursor = _desktop.GetCursorPosition();
        var monitor = WindowPlacementHelper.FindTargetMonitor(WindowPlacementMode.ScreenCenter, cursor, null, monitors, cursor)
            ?? throw new InvalidOperationException("History placement unavailable.");
        double scale = monitor.DpiScale;
        MinWidth = Math.Min(280, monitor.WorkArea.Width / scale);
        MinHeight = Math.Min(240, monitor.WorkArea.Height / scale);
        Width = Math.Min(Width, monitor.WorkArea.Width / scale);
        Height = Math.Min(Height, monitor.WorkArea.Height / scale);
        var bounds = WindowPlacementHelper.CalculateBounds(WindowPlacementMode.ScreenCenter, cursor, null, monitor,
            (int)Math.Round(Width * scale), (int)Math.Round(Height * scale), cursor);
        if (!_placement.SetWindowBounds(new WindowInteropHelper(this).Handle, bounds, show: false, activate: false))
            throw new InvalidOperationException("History placement failed.");
        ApplyResponsiveLayout(Width);
    }

    private void ApplyResponsiveLayout(double? targetWidth = null)
    {
        double width = targetWidth ?? (ActualWidth > 0 ? ActualWidth : Width);
        bool isWide = width >= WideLayoutBreakpoint;
        if (_layoutApplied && isWide == IsWideLayout) return;

        if (isWide)
        {
            HistoryScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            DetailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            LeftColumnDefinition.Width = new GridLength(324);
            RightColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            MainRowDefinition.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetColumnSpan(ConfirmationPanel, 2);
            Grid.SetRow(RightPane, 1);
            Grid.SetColumn(RightPane, 1);
            RightPane.BorderThickness = new Thickness(1, 0, 0, 0);
            RightPane.Margin = new Thickness(14, 0, 0, 0);
            RightPane.Padding = new Thickness(14, 0, 0, 0);
            HistoryList.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            HistoryScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            DetailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            LeftColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            RightColumnDefinition.Width = new GridLength(0);
            MainRowDefinition.Height = GridLength.Auto;
            Grid.SetColumnSpan(ConfirmationPanel, 1);
            Grid.SetRow(RightPane, 2);
            Grid.SetColumn(RightPane, 0);
            RightPane.BorderThickness = new Thickness(0, 1, 0, 0);
            RightPane.Margin = new Thickness(0, 10, 0, 0);
            RightPane.Padding = new Thickness(0, 10, 0, 0);
            HistoryList.MaxHeight = 240;
        }

        _layoutApplied = true;
        IsWideLayout = isWide;
    }
}

