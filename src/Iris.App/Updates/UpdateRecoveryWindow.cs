using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Iris.App.Settings;
using Wpf.Ui.Appearance;

namespace Iris.App.Updates;

/// <summary>A shutdown-owned recovery entry with no settings scope or service commands.</summary>
internal sealed class UpdateRecoveryWindow : Window
{
    private readonly TextBlock _detail;
    private readonly TextBlock _cleanup;
    private readonly Button _retry;
    private readonly Button _cancel;
    internal AsyncRelayCommand RetryCommand { get; }
    internal AsyncRelayCommand CancelUpdateCommand { get; }
    internal string FailureText => _detail.Text;

    internal UpdateRecoveryWindow(Func<Task> retry, Func<Task> cancel)
    {
        Title = UiText.UpdateTitle;
        Width = 540; MinWidth = 360; MaxWidth = 740;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        RetryCommand = new AsyncRelayCommand(retry, onException: _ => { });
        CancelUpdateCommand = new AsyncRelayCommand(cancel, onException: _ => { });
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        panel.Children.Add(new TextBlock { Text = UiText.UpdateFailed, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        _detail = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_detail);
        _cleanup = new TextBlock { Text = UiText.UpdateRecoveryCleanupPending, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        _cleanup.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(_cleanup);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _retry = new Wpf.Ui.Controls.Button { Content = UiText.UpdateRecoveryRetry, Command = RetryCommand,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(16, 6, 16, 6), MinHeight = 32, Margin = new Thickness(0, 0, 8, 4) };
        _cancel = new Wpf.Ui.Controls.Button { Content = UiText.UpdateRecoveryCancel, Command = CancelUpdateCommand,
            Padding = new Thickness(16, 6, 16, 6), MinHeight = 32, Margin = new Thickness(0, 0, 0, 4) };
        actions.Children.Add(_retry); actions.Children.Add(_cancel); panel.Children.Add(actions);
        Content = panel;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            if (handle == 0) return;
            int dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    internal void ShowFailure(string version, bool cleanupPending)
    {
        _detail.Text = UiText.UpdateRecoveryRequired + "\n\n" + version;
        _cleanup.Visibility = cleanupPending ? Visibility.Visible : Visibility.Collapsed;
        SetBusy(false);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }

    internal void SetBusy(bool busy)
    {
        _retry.IsEnabled = !busy;
        _cancel.IsEnabled = !busy;
    }
}
