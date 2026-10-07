using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

[Collection("ScreenshotRendering")]
public sealed class OcrSettingsRenderingTests
{
    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };
    [Fact]
    public async Task PagesAndLifecycleStates_RenderAtActualSizesInBothThemes()
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("IRIS_T032B_EVIDENCE") ?? Path.GetTempPath(), "settings-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var manifest = new List<object>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            foreach (var size in new[] { new Size(600, 440), new Size(760, 520) })
            {
                await Run(async f =>
                {
                    Prepare(f, theme, size); f.Show();
                    f.Vm.SelectedOcrProfile!.DisplayName.Value = "English OCR service with a long descriptive label · 中文识别服务示例";
                    f.Vm.SelectedOcrProfile.Credentials.Value = CredentialEditKind.Replace;
                    ((PasswordBox)f.Window.FindName("OcrApiKeyPasswordBox")).Password = "synthetic-masked-api";
                    ((PasswordBox)f.Window.FindName("OcrSecretKeyPasswordBox")).Password = "synthetic-masked-secret";
                    var scroll = (ScrollViewer)f.Window.FindName("OcrPageScroll");
                    scroll.ScrollToTop(); await Render(f, $"{theme}-{size.Width}-ocr-top", directory, manifest);
                    ((ListBox)f.Window.FindName("OcrProfilesListBox")).BringIntoView(); await Render(f, $"{theme}-{size.Width}-ocr-list", directory, manifest);
                    ((TextBox)f.Window.FindName("OcrBaseUrlTextBox")).BringIntoView(); await Render(f, $"{theme}-{size.Width}-baidu-options", directory, manifest);
                    scroll.ScrollToEnd(); await Render(f, $"{theme}-{size.Width}-baidu-fields", directory, manifest);
                    f.Vm.SelectedOcrProfile = f.Vm.OcrProfiles[1]; f.Vm.SelectedOcrProfile.LanguageTag.Value = "en-US";
                    scroll.ScrollToEnd(); await Render(f, $"{theme}-{size.Width}-windows-fields", directory, manifest);
                    f.Show("screenshot"); f.Vm.Screenshot.MaskOpacityText.Value = "0.8";
                    await Render(f, $"{theme}-{size.Width}-screenshot", directory, manifest);
                    if (size.Width == 600)
                    {
                        ((ScrollViewer)f.Window.FindName("ScreenshotPageScroll")).ScrollToEnd();
                        await Render(f, $"{theme}-{size.Width}-screenshot-bottom", directory, manifest);
                    }
                }, new() { General = new() { Theme = theme } });
            }
            await Run(async f =>
            {
                Prepare(f, theme, new(760, 520)); f.Show();
                f.Vm.SelectedOcrProfile!.TimeoutText.Value = "not a number";
                await f.Vm.ExecuteSaveAsync(); ((TextBox)f.Window.FindName("OcrTimeoutTextBox")).BringIntoView();
                await Render(f, $"{theme}-invalid-timeout", directory, manifest);
                f.Vm.ExecuteReload(); f.Show("screenshot"); f.Vm.Screenshot.MaskOpacityText.Value = "NaN";
                await f.Vm.ExecuteSaveAsync(); await Render(f, $"{theme}-invalid-opacity", directory, manifest);
                f.Show(); var scroll = (ScrollViewer)f.Window.FindName("OcrPageScroll"); scroll.ScrollToEnd();
                var gate = f.NewGate(); var cleanupGate = f.NewGate();
                f.Cloud.Configure = p => { p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); }; p.BeforeDispose = cleanupGate.Pause; };
                var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Render(f, $"{theme}-testing", directory, manifest); gate.Open();
                await cleanupGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Vm.OcrTestStatus.ShouldBe(UiText.OcrEnding); f.Vm.HasOcrTestOwnership.ShouldBeTrue(); work.IsCompleted.ShouldBeFalse();
                f.Vm.SelectedOcrProfile!.TestMessage.ShouldBeNull();
                await Render(f, $"{theme}-ending-test", directory, manifest); cleanupGate.Open(); await work;
                f.Cloud.Configure = p => p.Test = _ => Task.FromResult(new ProviderTestResult(false, TimeSpan.Zero, Error: new(ErrorCode.AuthenticationFailed, "synthetic private")));
                await f.Vm.ExecuteOcrTestAsync(); await Render(f, $"{theme}-test-failure", directory, manifest);
                f.Cloud.Configure = p => { p.FailDispose = true; p.Test = _ => Task.FromResult(new ProviderTestResult(false, TimeSpan.Zero, Error: new(ErrorCode.NetworkUnavailable, "synthetic private"))); };
                await f.Vm.ExecuteOcrTestAsync(); f.Vm.HasFailedOcrCleanup.ShouldBeTrue();
                await Render(f, $"{theme}-cleanup-failure", directory, manifest);
                f.Window.Width = 600; f.Window.Height = 440;
                ((Border)f.Window.FindName("NavScreenshot")).BringIntoView(); scroll.ScrollToEnd();
                await Render(f, $"{theme}-600-cleanup-failure", directory, manifest);
                f.Window.Width = 760; f.Window.Height = 520;
                foreach (var p in f.Cloud.Created) p.FailDispose = false;
                await f.Vm.RetryOcrCleanupAsync(); f.Cloud.Configure = null;
                f.Vm.Screenshot.MaskOpacityText.Value = "0.7"; f.ExtraObserver.Fail = true;
                await f.Vm.ExecuteSaveAsync(); f.Receipts.PendingCount.ShouldBe(1);
                await Render(f, $"{theme}-saved-apply-pending", directory, manifest);
            }, new() { General = new() { Theme = theme } });
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestOptions));
    }

    private static void Prepare(OcrSettingsUiScope f, AppTheme theme, Size size)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme == AppTheme.Dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(f.Window);
        f.Window.Width = size.Width; f.Window.Height = size.Height;
    }
    private static async Task Render(OcrSettingsUiScope f, string name, string directory, List<object> manifest)
    {
        f.Window.UpdateLayout(); await Frame(f.Window); await Frame(f.Window);
        await f.Window.Dispatcher.InvokeAsync(f.Window.UpdateLayout, DispatcherPriority.ContextIdle);
        var size = new Size(Math.Ceiling(f.Window.ActualWidth), Math.Ceiling(f.Window.ActualHeight));
        var save = Descendants<Wpf.Ui.Controls.Button>(f.Window).Single(b => Equals(b.Content, UiText.SettingsSave));
        var bottomRight = save.TransformToAncestor(f.Window).Transform(new Point(save.ActualWidth, save.ActualHeight));
        bottomRight.X.ShouldBeLessThanOrEqualTo(size.Width); bottomRight.Y.ShouldBeLessThanOrEqualTo(size.Height);
        foreach (var input in Descendants<TextBox>(f.Window).Where(t => t.IsVisible && t.ActualWidth > 0))
        {
            var edge = input.TransformToAncestor(f.Window).Transform(new Point(input.ActualWidth, 0));
            edge.X.ShouldBeLessThanOrEqualTo(size.Width);
        }
        var render = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        render.Render(f.Window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        string path = Path.Combine(directory, name + ".png"); using (var file = File.Create(path)) encoder.Save(file);
        manifest.Add(new { File = Path.GetFileName(path), Width = size.Width, Height = size.Height, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
    }
    private static async Task Frame(FrameworkElement element)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, EventArgs args) => ready.TrySetResult();
        CompositionTarget.Rendering += Handler;
        try { element.InvalidateVisual(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Handler; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
