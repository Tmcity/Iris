using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Settings;
using Shouldly;

namespace Iris.App.Tests.Settings;

public sealed class SelectionSettingsRenderingTests
{
    [Fact]
    public async Task SelectionPage_RendersBothThemesAndSizes_WithErrorAndLongList()
    {
        string directory = Path.Combine(Path.GetTempPath(), "iris-t023b-render"); Directory.CreateDirectory(directory);
        var manifest = new List<string>();
        foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
        {
            foreach (var size in new[] { new Size(600, 440), new Size(760, 520) })
            {
                await SelectionSettingsTestScope.Run(async f =>
                {
                f.Vm.Selection.ClipboardUnsafeProcessesText = string.Join('\n', Enumerable.Range(1, 64).Select(i => $"Long application process name {i:000} with additional extension.exe"));
                f.Vm.Selection.ClipboardTimeoutText = "not an integer";
                await f.Vm.ExecuteSaveAsync();
                f.Vm.Selection.HasError.ShouldBeTrue(); f.Vm.StatusMessage.ShouldBe(UiText.SelectionTimeoutError);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(f.Window);
                f.Window.Width = size.Width; f.Window.Height = size.Height; f.Show();
                var scroll = (ScrollViewer)f.Window.FindName("SelectionScrollViewer");
                foreach (bool bottom in new[] { false, true })
                {
                    if (bottom) scroll.ScrollToEnd(); else scroll.ScrollToTop();
                    f.Window.UpdateLayout();
                    await NextFrame(f.Window); await NextFrame(f.Window);
                    await f.Window.Dispatcher.InvokeAsync(() => f.Window.UpdateLayout(), DispatcherPriority.ContextIdle);
                    var textbox = (TextBox)f.Window.FindName("SelectionProcessesTextBox");
                    textbox.ActualWidth.ShouldBeGreaterThan(100);
                    textbox.TransformToAncestor(f.Window).Transform(new Point(textbox.ActualWidth, 0)).X.ShouldBeLessThanOrEqualTo(size.Width);
                    var save = Descendants<Wpf.Ui.Controls.Button>(f.Window).Single(b => Equals(b.Content, UiText.SettingsSave));
                    save.TransformToAncestor(f.Window).Transform(new Point(save.ActualWidth, save.ActualHeight)).Y.ShouldBeLessThanOrEqualTo(size.Height);
                    save.IsEnabled.ShouldBeTrue(); scroll.ScrollableHeight.ShouldBeGreaterThan(0);
                    var foreground = (SolidColorBrush)f.Window.FindResource("TextFillColorPrimaryBrush");
                    if (theme == Wpf.Ui.Appearance.ApplicationTheme.Dark) foreground.Color.R.ShouldBeGreaterThan((byte)150);
                    else foreground.Color.R.ShouldBeLessThan((byte)120);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen())
                        drawing.DrawRectangle(theme == Wpf.Ui.Appearance.ApplicationTheme.Light ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(size));
                    bitmap.Render(background); bitmap.Render(f.Window);
                    var pixels = new byte[(int)size.Width * (int)size.Height * 4];
                    bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
                    int sample = ((int)size.Width * 50 + 150) * 4;
                    if (theme == Wpf.Ui.Appearance.ApplicationTheme.Dark) pixels[sample + 2].ShouldBeLessThan((byte)120);
                    else pixels[sample + 2].ShouldBeGreaterThan((byte)130);
                    string path = Path.Combine(directory, $"selection-{theme}-{size.Width}x{size.Height}-{(bottom ? "bottom" : "top")}.png");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(path)) encoder.Save(file);
                    manifest.Add($"{Path.GetFileName(path)} {size.Width}x{size.Height} SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
                }
                });
            }
        }
        File.WriteAllLines(Path.Combine(directory, "manifest.txt"), manifest);
    }

    private static async Task NextFrame(FrameworkElement element)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Rendered(object? sender, EventArgs args) => frame.TrySetResult();
        CompositionTarget.Rendering += Rendered;
        try { element.InvalidateVisual(); await frame.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
