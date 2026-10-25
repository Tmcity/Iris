using System.Windows;
using System.Windows.Controls;
using Iris.App.Settings;

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
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = UiText.UpdateFailed, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) };
        panel.Children.Add(_detail);
        _cleanup = new TextBlock { Text = UiText.UpdateRecoveryCleanupPending, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(_cleanup);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _retry = new Wpf.Ui.Controls.Button { Content = UiText.UpdateRecoveryRetry, Command = RetryCommand,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        _cancel = new Wpf.Ui.Controls.Button { Content = UiText.UpdateRecoveryCancel, Command = CancelUpdateCommand, Padding = new Thickness(12, 6, 12, 6) };
        actions.Children.Add(_retry); actions.Children.Add(_cancel); panel.Children.Add(actions);
        Content = panel;
    }

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
