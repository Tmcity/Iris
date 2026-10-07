using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Iris.App.Translation;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Wpf.Ui.Controls;

namespace Iris.App.History;

internal partial class HistoryWindow : FluentWindow
{
    private readonly IDesktopContext _desktop;
    private readonly INativeWindowPlacementAdapter _placement;
    internal bool ForceClose { get; set; }
    public HistoryWindow(IDesktopContext desktop, INativeWindowPlacementAdapter placement)
    {
        _desktop = desktop; _placement = placement;
        InitializeComponent();
        SourceInitialized += (_, _) => PlaceInWorkArea();
        DpiChanged += (_, _) => PlaceInWorkArea();
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
    }
}
