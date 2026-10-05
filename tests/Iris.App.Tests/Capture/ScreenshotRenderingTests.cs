using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Capture;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.App.Tests.Capture;

[CollectionDefinition("ScreenshotRendering", DisableParallelization = true)]
public sealed class ScreenshotRenderingGroup;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotRenderingTests
{
    [Fact]
    public async Task ActualWindows_RenderThemesMagnifierEdgesAndMaskVariants()
    {
        string directory = Path.Combine(Path.GetTempPath(), "iris-t026b-render"); Directory.CreateDirectory(directory);
        var manifest = new List<string>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        foreach (var scenario in new[] { "none", "selection", "top-left", "top-right", "bottom-left", "bottom-right", "mask0", "mask90", "no-magnifier", "small", "long" })
        {
            await ScreenshotTestScope.Run(async f =>
            {
                int width = scenario == "small" ? 80 : scenario == "long" ? 4096 : 640;
                int height = scenario == "small" ? 60 : 360;
                var bounds = new Iris.Core.Imaging.PixelRect(-240, -120, width, height);
                f.Monitors = [new MonitorInfo("synthetic", bounds, bounds, 1, true)];
                await f.Settings(new() { ConfirmOnMouseUp = false, ShowMagnifier = scenario != "no-magnifier",
                    MaskOpacity = scenario == "mask0" ? 0 : scenario == "mask90" ? .9 : .45 }, theme);
                var task = f.Start(); await f.Ready(); var window = f.Service.Windows.Single();
                if (scenario is "selection" or "no-magnifier")
                    f.Select(bounds.X + 100, bounds.Y + 80, bounds.X + 500, bounds.Y + 280);
                else if (scenario is "long" or "small")
                    f.Select(bounds.X, bounds.Y, bounds.X + width - 1, bounds.Y + height - 1);
                int x = scenario.Contains("right", StringComparison.Ordinal) ? bounds.X + width - 1 : bounds.X;
                int y = scenario.Contains("bottom", StringComparison.Ordinal) ? bounds.Y + height - 1 : bounds.Y;
                if (scenario == "selection") { x = 20; y = 40; }
                f.Input(ScreenshotInput.Move, x, y);
                // Synthetic virtual screens can be wider than the test machine's native tracking limit.
                // Size this owned render window explicitly; physical placement itself remains fake.
                window.MinWidth = width; window.MinHeight = height; window.Width = width; window.Height = height;
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                window.ActualWidth.ShouldBe((double)width); window.ActualHeight.ShouldBe((double)height);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                string name = $"{theme}-{scenario}.png", path = Path.Combine(directory, name);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
                manifest.Add($"{name} {width}x{height} SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
                f.Input(ScreenshotInput.Escape); (await task).ShouldBeNull(); window.IsClosed.ShouldBeTrue();
            });
        }
        File.WriteAllLines(Path.Combine(directory, "manifest.txt"), manifest);
    }

    [Fact]
    public Task MagnifierSamplesOriginalPixelsWithRepeatedEdges() => ScreenshotTestScope.Run(async f =>
    {
        f.Monitors = [new("synthetic", new(-2, -3, 4, 4), new(-2, -3, 4, 4), 2, true)];
        var frame = f.Frame(); var view = await Task.Run(() => new ScreenshotFrameView(frame));
        foreach (var point in new[] { new Iris.Core.Imaging.PixelPoint(-2, -3), new Iris.Core.Imaging.PixelPoint(1, 0) })
        {
            var sample = view.Magnify(0, point); sample.PixelWidth.ShouldBe(15); sample.PixelHeight.ShouldBe(15);
            var pixels = new byte[15 * 15 * 4]; sample.CopyPixels(pixels, 60, 0);
            for (int y = 0; y < 15; y++) for (int x = 0; x < 15; x++)
            {
                int px = Math.Clamp(point.X + x - 7, -2, 1) + 2, py = Math.Clamp(point.Y + y - 7, -3, 0) + 3;
                pixels.AsSpan((y * 15 + x) * 4, 4).ToArray().ShouldBe(frame.Bgra32Pixels.AsSpan(py * frame.Stride + px * 4, 4).ToArray());
            }
        }
    });
}
