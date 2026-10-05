using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Selection;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Selection;

[CollectionDefinition("MouseSelectionRendering", DisableParallelization = true)]
public sealed class MouseSelectionRenderingGroup;

[Collection("MouseSelectionRendering")]
public sealed class MouseRenderingTests
{
    [Fact]
    public async Task ActualThemesAndSizes_RenderLongListsErrorsAllRetriesAndIcon()
    {
        string directory = Path.Combine(Path.GetTempPath(), "iris-t024b2-render"); Directory.CreateDirectory(directory);
        var manifest = new List<string>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            await MouseInteractionTestScope.Run(async f =>
            {
                await f.Controller.StartAsync();
                using var scope = f.Services.CreateScope();
                var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                var window = new SettingsWindow { ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, DataContext = vm };
                var themeService = f.Services.GetRequiredService<ThemeService>();
                var session = new SettingsWindowSession(scope, window, vm, themeService, scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>());
                try
                {
                    await f.Settings.UpdateAsync(s => s with { General = s.General with { Theme = theme } }); themeService.Apply(theme);
                    f.Mouse.InstallFailure = true;
                    f.Services.GetRequiredService<IAutoStartService>().When(a => a.SetEnabled(true)).Do(_ => throw new IOException("fake startup failure"));
                    vm.LaunchAtStartup = true; vm.Selection.MouseMode = MouseSelectionMode.ShowIcon; await vm.ExecuteSaveAsync();
                    vm.CanRetryAutoStart.ShouldBeTrue(); vm.CanRetryMouse.ShouldBeTrue();
                    var receipts = f.Services.GetRequiredService<ISettingsApplyReceiptManager>();
                    var receipt = Substitute.For<ISettingsApplyReceipt>(); receipt.CommitId.Returns(Guid.NewGuid());
                    receipts.TryReserveSlot().ShouldBeTrue(); receipts.ConvertReservationToPending(receipt, "服务应用尚未完成");
                    // Layout-only synthesis of the already independently tested hotkey failure flag.
                    typeof(SettingsViewModel).GetProperty(nameof(vm.CanRetryHotkeys))!.SetValue(vm, true);
                    vm.Selection.ExcludedProcessesText = string.Join('\n', Enumerable.Range(1, 64).Select(i => $"Long application process name {i:000} with extra extension.exe"));
                    vm.Selection.MinDragDistanceText = "invalid integer"; await vm.ExecuteSaveAsync();
                    vm.CurrentPageKey = "selection";
                    window.Show(); Wpf.Ui.Appearance.ApplicationThemeManager.Apply(window);
                    foreach (var size in new[] { new Size(600, 440), new Size(760, 520) })
                    {
                        window.Width = size.Width; window.Height = size.Height;
                        var scroll = (ScrollViewer)window.FindName("SelectionScrollViewer");
                        foreach (string region in new[] { "top", "list", "bottom" })
                        {
                            window.UpdateLayout();
                            if (region == "top") scroll.ScrollToTop();
                            else if (region == "bottom") scroll.ScrollToEnd();
                            else ((TextBox)window.FindName("MouseExcludedTextBox")).BringIntoView();
                            window.UpdateLayout(); await Frame(window); await Frame(window);
                            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                            foreach (string label in new[] { UiText.MouseRetry, UiText.SettingsRetry, UiText.SettingsRetryPendingApply, UiText.SettingsRetryHotkeys, UiText.SettingsSave, UiText.SettingsCancel })
                            {
                                var button = Descendants<Button>(window).Single(b => Equals(b.Content, label));
                                button.IsVisible.ShouldBeTrue(); var bottomRight = button.TransformToAncestor(window).Transform(new Point(button.ActualWidth, button.ActualHeight));
                                bottomRight.X.ShouldBeLessThanOrEqualTo(size.Width); bottomRight.Y.ShouldBeLessThanOrEqualTo(size.Height);
                            }
                            scroll.ScrollableHeight.ShouldBeGreaterThan(0);
                            Save(window, size, Path.Combine(directory, $"mouse-settings-{theme}-{size.Width}x{size.Height}-{region}.png"), manifest);
                        }
                    }
                    vm.Selection.MinDragDistanceText = "6"; f.Mouse.InstallFailure = false; await f.Controller.RetryAsync();
                    await f.ShowCandidate(); var icon = f.Factory.Windows.Single();
                    f.Settings.Current.General.Theme.ShouldBe(theme);
                    themeService.Apply(theme); Wpf.Ui.Appearance.ApplicationThemeManager.Apply(icon);
                    icon.ShowActivated.ShouldBeFalse(); icon.ShowInTaskbar.ShouldBeFalse();
                    bool handled = false; SelectionIconWindow.WindowMessage(0, 0x21, 0, 0, ref handled).ShouldBe((nint)3); handled.ShouldBeTrue();
                    await Frame(icon);
                    var foreground = (SolidColorBrush)icon.FindResource("TextFillColorPrimaryBrush");
                    if (theme == AppTheme.Light) foreground.Color.R.ShouldBeLessThan((byte)120);
                    else foreground.Color.R.ShouldBeGreaterThan((byte)150);
                    Save(icon, new Size(24, 24), Path.Combine(directory, $"mouse-icon-{theme}.png"), manifest);
                }
                finally { await session.ShutdownAsync(); window.ForceClose = true; window.Close(); }
            });
        }
        File.WriteAllLines(Path.Combine(directory, "manifest.txt"), manifest);
    }
    private static void Save(Visual visual, Size size, string path, List<string> manifest)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream);
        manifest.Add($"{Path.GetFileName(path)} {size.Width}x{size.Height} SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
    }
    private static async Task Frame(FrameworkElement element)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Rendered(object? sender, EventArgs args) => frame.TrySetResult();
        CompositionTarget.Rendering += Rendered;
        try { element.InvalidateVisual(); await frame.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
