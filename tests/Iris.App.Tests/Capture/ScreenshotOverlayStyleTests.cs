using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Capture;
using Wpf.Ui.Appearance;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotOverlayStyleTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public Task OVERLAY_STYLE_USES_ROUNDED_LABELS_SYSTEM_ACCENT_AND_MAGNIFIER_CHROME(int scale, bool dark) =>
        ScreenshotTestScope.Run(async f =>
        {
            try
            {
            ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
            f.Monitors = [new("synthetic", new(0, 0, 320 * scale, 240 * scale), new(0, 0, 320 * scale, 240 * scale), scale, true)];
            await f.Settings(new() { ConfirmOnMouseUp = false }, dark ? Iris.Core.Settings.AppTheme.Dark : Iris.Core.Settings.AppTheme.Light);
            var task = f.Start();
            await f.Ready();
            f.Select(90, 90, 109, 109);
            f.Input(ScreenshotInput.Move, 100, 100);

            var window = f.Service.Windows.Single();
            Assert.False(window.AllowsTransparency);
            Assert.Equal(Brushes.Black, window.Background);

            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);

            var drawing = VisualTreeHelper.GetDrawing((Visual)window.Content);
            Assert.NotNull(drawing);

            var items = Flatten(drawing!, Matrix.Identity, 1).ToArray();
            var magnifier = items.FirstOrDefault(item =>
                item.Drawing is ImageDrawing { ImageSource: BitmapSource bitmap } &&
                bitmap.PixelWidth == 15 && bitmap.PixelHeight == 15);
            Rect imageRect = magnifier.Drawing is ImageDrawing image
                ? Transform(image.Rect, magnifier.Transform)
                : Rect.Empty;

            var expectedChrome = dark ? Colors.Black : Colors.White;
            var labelDrawings = items
                .Where(item => item.Drawing is GeometryDrawing { Brush: SolidColorBrush brush } &&
                               brush.Color.A == 255 && brush.Opacity == 1 && item.Opacity == 1)
                .Select(item => (Geometry: ((GeometryDrawing)item.Drawing).Geometry,
                    Brush: (SolidColorBrush)((GeometryDrawing)item.Drawing).Brush!,
                    Bounds: Transform(((GeometryDrawing)item.Drawing).Geometry.Bounds, item.Transform)))
                .Where(x => imageRect.IsEmpty || !x.Bounds.Contains(imageRect))
                .ToArray();

            Assert.Equal(3, labelDrawings.Length);
            foreach (var label in labelDrawings)
            {
                var rectGeo = Assert.IsType<RectangleGeometry>(label.Geometry);
                Assert.Equal(ScreenshotCanvas.LabelCornerRadius * scale, rectGeo.RadiusX);
                Assert.Equal(ScreenshotCanvas.LabelCornerRadius * scale, rectGeo.RadiusY);
                Assert.Equal(expectedChrome, label.Brush.Color);
            }

            var expectedAccent = ScreenshotCanvas.ResolveSystemAccentColor();
            var accentDrawings = items
                .Where(item => item.Drawing is GeometryDrawing { Brush: null, Pen.Brush: SolidColorBrush penBrush } &&
                               penBrush.Color == expectedAccent)
                .ToArray();
            Assert.Equal(2, accentDrawings.Length);
            foreach (var accent in accentDrawings)
            {
                var geo = Assert.IsType<RectangleGeometry>(((GeometryDrawing)accent.Drawing).Geometry);
                Assert.Equal(0.5, geo.Rect.X - Math.Floor(geo.Rect.X));
                Assert.Equal(0.5, geo.Rect.Y - Math.Floor(geo.Rect.Y));
            }

            var magnifierFrame = items.FirstOrDefault(item =>
                item.Drawing is GeometryDrawing { Brush: SolidColorBrush brush, Pen: not null } &&
                brush.Color == expectedChrome &&
                !imageRect.IsEmpty &&
                Transform(((GeometryDrawing)item.Drawing).Geometry.Bounds, item.Transform).Contains(imageRect));
            Assert.NotNull(magnifierFrame.Drawing);

            var centerOuter = items.FirstOrDefault(item =>
                item.Drawing is GeometryDrawing { Brush: null, Pen: not null, Geometry: RectangleGeometry geo } &&
                geo.Rect.Width is >= 9 and <= 10 && geo.Rect.Height is >= 9 and <= 10);
            Assert.NotNull(centerOuter.Drawing);

            f.Input(ScreenshotInput.Escape);
            await task;
            }
            finally
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
            }
        });

    private static Rect Transform(Rect rectangle, Matrix transform)
    {
        rectangle.Transform(transform);
        return rectangle;
    }

    private static IEnumerable<(Drawing Drawing, Matrix Transform, double Opacity)> Flatten(
        Drawing drawing, Matrix transform, double opacity)
    {
        if (drawing is DrawingGroup group)
        {
            var next = group.Transform?.Value ?? Matrix.Identity;
            next.Append(transform);
            foreach (var child in group.Children)
                foreach (var item in Flatten(child, next, opacity * group.Opacity))
                    yield return item;
        }
        else
        {
            yield return (drawing, transform, opacity);
        }
    }
}
