using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Capture;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Wpf.Ui.Appearance;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotR1RenderingTests
{
    private static string DirectoryPath => Path.Combine(Path.GetTempPath(), "iris-t026b-r1-render");
    [Theory]
    [InlineData(1)][InlineData(2)]
    public Task SMALL_SELECTION_LABELS_DO_NOT_OVERLAP(int scale) => ScreenshotTestScope.Run(async f =>
        {
            Directory.CreateDirectory(DirectoryPath);
            f.Monitors = [new("synthetic", new(0, 0, 320 * scale, 240 * scale), new(0, 0, 320 * scale, 240 * scale), scale, true)];
            await f.Settings(new() { ConfirmOnMouseUp = false });
            var task = f.Start(); await f.Ready(); f.Select(90, 90, 109, 109); f.Input(ScreenshotInput.Move, 100, 100);
            var window = f.Service.Windows.Single();
            await Render(window, $"small-selection-labels-dpi{scale}.png");
            var drawing = VisualTreeHelper.GetDrawing((Visual)window.Content);
            Require(drawing != null, "actual label drawing was unavailable");
            var items = Flatten(drawing!, Matrix.Identity, 1).ToArray();
            var magnifier = items.FirstOrDefault(item => item.Drawing is ImageDrawing { ImageSource: BitmapSource bitmap } && bitmap.PixelWidth == 15 && bitmap.PixelHeight == 15);
            Rect imageRect = magnifier.Drawing is ImageDrawing image ? Transform(image.Rect, magnifier.Transform) : Rect.Empty;
            var labels = items.Where(item => item.Drawing is GeometryDrawing { Brush: SolidColorBrush brush } && brush.Color.A == 255 && brush.Opacity == 1 && item.Opacity == 1)
                .Select(item => Transform(((GeometryDrawing)item.Drawing).Geometry.Bounds, item.Transform))
                .Where(rect => imageRect.IsEmpty || !rect.Contains(imageRect)).ToArray();
            int overlaps = 0;
            for (int i = 0; i < labels.Length; i++)
            for (int j = i + 1; j < labels.Length; j++)
            {
                var overlap = Rect.Intersect(labels[i], labels[j]);
                if (!overlap.IsEmpty && overlap.Width * overlap.Height > .5) overlaps++;
            }
            Console.WriteLine($"LABEL_LAYOUT opaqueLabels={labels.Length} overlappingPairs={overlaps}");
            Require(overlaps == 0, "size and instruction labels overlap after avoiding the magnifier");
            f.Input(ScreenshotInput.Escape); await task;
        });

    [Theory]
    [InlineData(1)][InlineData(2)]
    public Task NATIVE_RIGHT_CLEAR_RETAINS_PHYSICAL_POINTER(int scale) => ScreenshotTestScope.Run(async f =>
        {
            Directory.CreateDirectory(DirectoryPath);
            f.Monitors = [new("synthetic", new(0, 0, 320 * scale, 240 * scale), new(0, 0, 320 * scale, 240 * scale), scale, true)];
            await f.Settings(new() { ConfirmOnMouseUp = false });
            var task = f.Start(); await f.Ready(); f.Select(90, 90, 109, 109); f.Input(ScreenshotInput.Move, 100, 100);
            var window = f.Service.Windows.Single();
            f.Native.ReviewPhysicalPoint = (_, _) => new(100, 100);
            try
            {
                await Render(window, $"right-clear-before-dpi{scale}.png");
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                window.RaiseEvent(mouse); // Actual registered Window mouse route, no SendInput or native capture.
                await Render(window, $"right-clear-after-dpi{scale}.png");
                var canvas = window.Content;
                var field = canvas.GetType().GetField("_pointer", BindingFlags.Instance | BindingFlags.NonPublic);
                Require(field != null, "final canvas pointer representation changed; adapt this observer before treating it as a product result");
                var displayed = (PixelPoint)field!.GetValue(canvas)!;
                Console.WriteLine($"RIGHT_CLEAR handled={mouse.Handled} displayedX={displayed.X} displayedY={displayed.Y}");
                Require(mouse.Handled && displayed == new PixelPoint(100, 100), "right-click clearing replaced the physical pointer with a default coordinate");
                f.Select(90, 90, 109, 109);
                f.Input(ScreenshotInput.Right, 100, 100);
                Require((PixelPoint)field.GetValue(canvas)! == new PixelPoint(100, 100), "wrapped right route changed the physical snapshot");
                f.Input(ScreenshotInput.Escape); await task;
            }
            finally { f.Native.ReviewPhysicalPoint = null; }
        });

    private static async Task<(byte[] Pixels, int Width, int Height)> Render(ScreenshotOverlayWindow window, string name)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
        int width = window.Monitor.Bounds.Width, height = window.Monitor.Bounds.Height;
        double dpi = 96 * window.Monitor.DpiScale;
        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(window);
        var bytes = new byte[width * height * 4]; target.CopyPixels(bytes, width * 4, 0);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(Path.Combine(DirectoryPath, name)); encoder.Save(stream);
        return (bytes, width, height);
    }
    private static Rect Transform(Rect rectangle, Matrix transform) { rectangle.Transform(transform); return rectangle; }
    private static IEnumerable<(Drawing Drawing, Matrix Transform, double Opacity)> Flatten(Drawing drawing, Matrix transform, double opacity)
    {
        if (drawing is DrawingGroup group)
        {
            var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
            foreach (var child in group.Children)
                foreach (var item in Flatten(child, next, opacity * group.Opacity)) yield return item;
        }
        else yield return (drawing, transform, opacity);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
