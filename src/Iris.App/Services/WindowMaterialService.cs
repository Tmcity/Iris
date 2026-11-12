using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace Iris.App.Services;

/// <summary>
/// Owns the acrylic backdrop of the main windows. Nothing else may set
/// <see cref="FluentWindow.WindowBackdropType"/>: the theme layer keeps the windows solid and
/// this service switches the three acrylic windows over after their handle exists.
/// </summary>
internal sealed class WindowMaterialService
{
    private const int Windows11_22H2Build = 22621;

    private readonly Func<int> _osBuild;
    private readonly Func<bool> _backdropSupported;

    public WindowMaterialService()
        : this(() => Environment.OSVersion.Version.Build, () => WindowBackdrop.IsSupported(WindowBackdropType.Acrylic))
    {
    }

    internal WindowMaterialService(Func<int> osBuild, Func<bool> backdropSupported)
    {
        _osBuild = osBuild ?? throw new ArgumentNullException(nameof(osBuild));
        _backdropSupported = backdropSupported ?? throw new ArgumentNullException(nameof(backdropSupported));
    }

    internal bool IsAcrylicSupported => _osBuild() >= Windows11_22H2Build && _backdropSupported();

    /// <summary>
    /// Applies the acrylic backdrop if the system supports it. Safe to call again after a theme
    /// change: the theme change tears the backdrop down and restores an opaque background brush,
    /// so both the backdrop and the transparent background are (re-)applied here.
    /// </summary>
    internal void ApplyAcrylic(FluentWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!IsAcrylicSupported)
        {
            return;
        }

        if (window.WindowBackdropType != WindowBackdropType.Acrylic)
        {
            window.WindowBackdropType = WindowBackdropType.Acrylic;
            return;
        }

        // The backdrop does NOT survive a theme change: ApplicationThemeManager.Apply(theme, None)
        // runs WindowBackgroundManager.UpdateBackground on Application.MainWindow, which removes the
        // DWM backdrop and restores an opaque Background brush. Re-run WPF-UI's own apply path here
        // rather than clearing the brush by hand: setting the type away and back re-applies the
        // backdrop, removes the background brush, and still honours the veto WPF-UI puts on acrylic
        // in high-contrast themes - which a bare WindowBackdrop.ApplyBackdrop call would bypass.
        // Measured on the test host (it merges the same dictionaries App.xaml does):
        // DWMWA_SYSTEMBACKDROP_TYPE 3 -> 1 on the theme change, back to 3 after this.
        window.WindowBackdropType = WindowBackdropType.None;
        window.WindowBackdropType = WindowBackdropType.Acrylic;
    }

    /// <summary>
    /// Subscribes to the window so the backdrop is applied as soon as the handle exists.
    /// Called by the presenter right after it resolves the window. Idempotent.
    /// </summary>
    internal void Attach(FluentWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!IsAcrylicSupported)
        {
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            ApplyAcrylic(window);
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Re-applies the backdrop to every open acrylic window. Called after the theme changed.
    /// </summary>
    internal void RefreshOpenWindows()
    {
        if (Application.Current is not { } application || !IsAcrylicSupported)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            // ExtendsContentIntoTitleBar is the precondition for a backdrop at all (WPF-UI throws
            // when it is false), so this filter cannot miss an acrylic window - it is the same test
            // WPF-UI applies internally, not just "the three windows we know about".
            if (window is FluentWindow fluent && fluent.ExtendsContentIntoTitleBar)
            {
                ApplyAcrylic(fluent);
            }
        }
    }
}
