using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Capture;
using Iris.App.Tests.Capture;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Wpf.Ui.Appearance;

internal static class VisualChecks
{
    private static string DirectoryPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "visuals"));

    internal static async Task Run(Func<string, Func<Task>, Task> run)
    {
        Directory.CreateDirectory(DirectoryPath);
        await run("SYSTEM_USES_CURRENT_RESOLVED_DARK_VISUAL_THEME", async () =>
        {
            byte[]? explicitDark = null, systemDark = null;
            await ScreenshotTestScope.Run(async f =>
            {
                await f.Settings(new(), AppTheme.Dark);
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                var task = f.Start(); await f.Ready();
                explicitDark = (await Render(f.Service.Windows.Single(), "explicit-dark.png")).Pixels;
                f.Input(ScreenshotInput.Escape); await task;
            });
            await ScreenshotTestScope.Run(async f =>
            {
                // The fake settings start at System; setting the owned test application's resolved
                // resources to Dark models a supported system outcome without changing OS preferences.
                Require(f.A.Mouse.Settings.Current.General.Theme == AppTheme.System, "fake settings no longer begin in System mode");
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                Require(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, "owned application's resolved dark theme was not established");
                var task = f.Start(); await f.Ready();
                try
                {
                    systemDark = (await Render(f.Service.Windows.Single(), "system-resolved-dark.png")).Pixels;
                    f.Input(ScreenshotInput.Escape); await task;
                }
                finally { ApplicationThemeManager.Apply(ApplicationTheme.Light); }
            });
            int differences = explicitDark!.Zip(systemDark!, (a, b) => a == b ? 0 : 1).Sum();
            Console.WriteLine($"RESOLVED_THEME explicitDarkBytes={explicitDark!.Length} systemDarkBytes={systemDark!.Length} differingBytes={differences}");
            Require(explicitDark!.Length == systemDark!.Length && differences == 0, "System rendered a different chrome theme despite the same resolved application visuals");
        });

        foreach (bool small in new[] { false, true })
            await run("MAGNIFIER_LAYOUT_DPI2_" + (small ? "SMALL" : "NORMAL"), () => ScreenshotTestScope.Run(async f =>
            {
                int width = small ? 160 : 480, height = small ? 150 : 360;
                f.Monitors = [new("synthetic-layout", new(0, 0, width, height), new(0, 0, width, height), 2, true)];
                await f.Settings(new() { ConfirmOnMouseUp = false }, AppTheme.Dark);
                var task = f.Start(); await f.Ready();
                f.Input(ScreenshotInput.Move, width / 2, height / 2);
                var window = f.Service.Windows.Single();
                var rendered = await Render(window, small ? "magnifier-small-dpi2.png" : "magnifier-normal-dpi2.png");
                var canvas = (Visual)window.Content;
                var drawing = VisualTreeHelper.GetDrawing(canvas);
                Require(drawing != null, "actual canvas render drawing was unavailable");
                var items = Flatten(drawing!, Matrix.Identity, 1).ToArray();
                int imageIndex = Array.FindIndex(items, item => item.Drawing is ImageDrawing { ImageSource: BitmapSource bitmap } && bitmap.PixelWidth == 15 && bitmap.PixelHeight == 15);
                if (imageIndex < 0)
                {
                    Require(small, "normal viewport omitted its configured magnifier");
                    Console.WriteLine("MAGNIFIER_LAYOUT small=True hiddenForLimitedSpace=True");
                }
                else
                {
                    var image = items[imageIndex];
                    var imageRect = Transform(((ImageDrawing)image.Drawing).Rect, image.Transform);
                    Require(Math.Abs(imageRect.Width * 2 - 120) < .01 && Math.Abs(imageRect.Height * 2 - 120) < .01, "magnifier did not render at eight physical pixels per source pixel");
                    int occluders = 0;
                    foreach (var item in items.Skip(imageIndex + 1))
                    {
                        if (item.Drawing is not GeometryDrawing { Brush: SolidColorBrush brush } geometry || brush.Color.A != 255 || brush.Opacity < 1 || item.Opacity < 1) continue;
                        var rectangle = Transform(geometry.Geometry.Bounds, item.Transform);
                        rectangle.Intersect(imageRect);
                        if (!rectangle.IsEmpty && rectangle.Width * rectangle.Height > .5) occluders++;
                    }
                    Console.WriteLine($"MAGNIFIER_LAYOUT small={small} physicalWidth={imageRect.Width * 2} physicalHeight={imageRect.Height * 2} opaqueLaterOverlaps={occluders} rendered={rendered.Width}x{rendered.Height}");
                    Require(occluders == 0, "a later opaque label/chrome overlaps the magnifier's source pixels");
                }
                f.Input(ScreenshotInput.Escape); await task;
            }));

        await run("SMALL_SELECTION_LABELS_DO_NOT_OVERLAP", () => ScreenshotTestScope.Run(async f =>
        {
            await f.Settings(new() { ConfirmOnMouseUp = false });
            var task = f.Start(); await f.Ready(); f.Select(90, 90, 109, 109); f.Input(ScreenshotInput.Move, 100, 100);
            var window = f.Service.Windows.Single();
            await Render(window, "small-selection-labels.png");
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
        }));

        await run("NATIVE_RIGHT_CLEAR_RETAINS_PHYSICAL_POINTER", () => ScreenshotTestScope.Run(async f =>
        {
            await f.Settings(new() { ConfirmOnMouseUp = false });
            var task = f.Start(); await f.Ready(); f.Select(90, 90, 109, 109); f.Input(ScreenshotInput.Move, 100, 100);
            var window = f.Service.Windows.Single();
            f.Native.ReviewPhysicalPoint = (_, _) => new(100, 100);
            try
            {
                await Render(window, "right-clear-before.png");
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                window.RaiseEvent(mouse); // Actual registered Window mouse route, no SendInput or native capture.
                await Render(window, "right-clear-after.png");
                var canvas = window.Content;
                var field = canvas.GetType().GetField("_pointer", BindingFlags.Instance | BindingFlags.NonPublic);
                Require(field != null, "final canvas pointer representation changed; adapt this observer before treating it as a product result");
                var displayed = (PixelPoint)field!.GetValue(canvas)!;
                Console.WriteLine($"RIGHT_CLEAR handled={mouse.Handled} displayedX={displayed.X} displayedY={displayed.Y}");
                Require(mouse.Handled && displayed == new PixelPoint(100, 100), "right-click clearing replaced the physical pointer with a default coordinate");
                f.Input(ScreenshotInput.Escape); await task;
            }
            finally { f.Native.ReviewPhysicalPoint = null; }
        }));
    }
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
