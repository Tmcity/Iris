using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Capture;
using Iris.App.Tests.Capture;
using Iris.Core.Imaging;
using Iris.Core.Platform;

internal static class GeometryChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> run)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cases.json")));
        var root = document.RootElement;
        var monitors = root.GetProperty("monitors").EnumerateArray().Select(m =>
            new MonitorInfo(m.GetProperty("id").GetString()!, Rect(m.GetProperty("bounds")), Rect(m.GetProperty("bounds")),
                m.GetProperty("dpiScale").GetDouble(), m.GetProperty("primary").GetBoolean())).ToArray();
        var bounds = Rect(root.GetProperty("virtualBounds"));
        foreach (var row in root.GetProperty("dragCases").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            int monitor = row.GetProperty("monitor").GetInt32();
            var down = Point(row.GetProperty("down")); var up = Point(row.GetProperty("up"));
            PixelRect? expected = row.GetProperty("region").ValueKind == JsonValueKind.Null ? null : Rect(row.GetProperty("region"));
            await run("DRAG_" + name, () => ScreenshotTestScope.Run(async f =>
            {
                f.Monitors = monitors;
                f.ReviewFrameFactory = () => Frame(monitors, bounds);
                await f.Settings(new() { ShowMagnifier = true });
                var task = f.Start(); await f.Ready();
                Require(f.Service.Windows.Count == monitors.Length && f.Service.Windows.All(w => w.IsVisible), "not every actual monitor window is visible");
                f.Select(down.X, down.Y, up.X, up.Y, monitor);
                if (expected == null)
                {
                    // A later valid selection proves the invalid input did not settle this session,
                    // without guessing ThreadPool timing from a short IsCompleted check.
                    f.Input(ScreenshotInput.Enter, screen: monitor);
                    expected = new(bounds.X + 1, bounds.Y + 1, 4, 4);
                    f.Select(expected.Value.X, expected.Value.Y, expected.Value.X + 3, expected.Value.Y + 3, 0);
                }
                var result = await task;
                Require(result != null && result.Region == expected.Value, "returned physical rectangle differs from literal specification case");
                CheckPng(result!.Image, result.Region, bounds, gdi: false);
                Require(f.Capturer.Captures == 1 && f.Capturer.Crops == 1, "selection refroze or encoded more than once");
                Require(f.Native.Windows.All(w => w.IsClosed && w.Handle == 0) && f.Native.Captured == null, "native view/capture responsibility remained");
            }));
        }
        foreach (var row in root.GetProperty("moveCases").EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!;
            int monitor = row.GetProperty("monitor").GetInt32();
            var initial = Rect(row.GetProperty("initial")); var expected = Rect(row.GetProperty("region"));
            var down = Point(row.GetProperty("down")); var up = Point(row.GetProperty("up"));
            await run("MOVE_" + name, () => ScreenshotTestScope.Run(async f =>
            {
                f.Monitors = monitors; f.ReviewFrameFactory = () => Frame(monitors, bounds);
                await f.Settings(new() { ConfirmOnMouseUp = false });
                var task = f.Start(); await f.Ready();
                f.Select(initial.X, initial.Y, initial.X + initial.Width - 1, initial.Y + initial.Height - 1, monitor);
                f.Input(ScreenshotInput.Down, down.X, down.Y, monitor);
                f.Input(ScreenshotInput.Move, up.X, up.Y, monitor);
                f.Input(ScreenshotInput.Up, up.X, up.Y, monitor);
                f.Input(ScreenshotInput.Enter, screen: monitor);
                f.Input(ScreenshotInput.Enter, screen: monitor);
                var result = await task;
                Require(result != null && result.Region == expected, "manual move did not preserve size or clamp physical rectangle");
                CheckPng(result!.Image, expected, bounds, false);
                Require(f.Capturer.Crops == 1, "duplicate confirmation encoded twice");
            }));
        }
        await run("ACTUAL_GDI_FRAME_TO_WINDOW_TO_REAL_PNG", () => ScreenshotTestScope.Run(async f =>
        {
            f.Monitors = monitors;
            var task = f.Start(); await f.Ready();
            f.Select(-419, -219, -416, -216);
            var result = await task;
            Require(result != null && result.Region == new PixelRect(-419, -219, 4, 4), "GDI chain returned wrong selection");
            CheckPng(result!.Image, result.Region, bounds, true);
            Require(f.Codec.Native.Count("BitBlt") == monitors.Length && f.Codec.Native.Handles == 0, "GDI frame was not captured/released as expected");
        }, gdi: true));
        await run("DOUBLE_CLICK_CONFIRMS_REAL_WINDOW_ONCE", () => ScreenshotTestScope.Run(async f =>
        {
            await f.Settings(new() { ConfirmOnMouseUp = false });
            var task = f.Start(); await f.Ready(); f.Select(10, 20, 19, 29);
            f.Input(ScreenshotInput.DoubleClick, 15, 25); f.Input(ScreenshotInput.DoubleClick, 15, 25);
            var result = await task;
            Require(result?.Region == new PixelRect(10, 20, 10, 10) && f.Capturer.Crops == 1, "double click did not confirm exactly once");
        }));
    }
    private static PixelRect Rect(JsonElement item) { var n = item.EnumerateArray().Select(x => x.GetInt32()).ToArray(); return new(n[0], n[1], n[2], n[3]); }
    private static PixelPoint Point(JsonElement item) { var n = item.EnumerateArray().Select(x => x.GetInt32()).ToArray(); return new(n[0], n[1]); }
    private static bool Contains(PixelRect r, long x, long y) => x >= r.X && y >= r.Y && x < (long)r.X + r.Width && y < (long)r.Y + r.Height;
    private static CapturedFrame Frame(MonitorInfo[] monitors, PixelRect bounds)
    {
        int stride = bounds.Width * 4 + 16;
        var bytes = Enumerable.Repeat((byte)231, stride * bounds.Height).ToArray();
        for (int y = 0; y < bounds.Height; y++)
        for (int x = 0; x < bounds.Width; x++)
        {
            int offset = y * stride + x * 4;
            bool screen = monitors.Any(m => Contains(m.Bounds, (long)x + bounds.X, (long)y + bounds.Y));
            bytes[offset] = screen ? (byte)(x % 251) : (byte)0;
            bytes[offset + 1] = screen ? (byte)(y % 241) : (byte)0;
            bytes[offset + 2] = screen ? (byte)((7 * x + 11 * y) % 239) : (byte)0;
            bytes[offset + 3] = 255;
        }
        return new(bytes, bounds.Width, bounds.Height, stride, bounds, monitors, DateTimeOffset.UnixEpoch);
    }
    private static void CheckPng(ImageData image, PixelRect region, PixelRect virtualBounds, bool gdi)
    {
        Require(image.Format == ImageFormat.Png && image.PixelWidth == region.Width && image.PixelHeight == region.Height, "PNG metadata mismatch");
        using var stream = new MemoryStream(image.Bytes, writable: false);
        var source = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Require(source.PixelWidth == region.Width && source.PixelHeight == region.Height, "actual decoded dimensions mismatch");
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[region.Width * region.Height * 4]; converted.CopyPixels(pixels, region.Width * 4, 0);
        for (int y = 0; y < region.Height; y++)
        for (int x = 0; x < region.Width; x++)
        {
            int at = (y * region.Width + x) * 4;
            int localX = region.X + x - virtualBounds.X, localY = region.Y + y - virtualBounds.Y;
            byte b = gdi ? unchecked((byte)(region.X + x)) : (byte)(localX % 251);
            byte g = gdi ? unchecked((byte)(region.Y + y)) : (byte)(localY % 241);
            byte r = gdi ? (byte)37 : (byte)((7 * localX + 11 * localY) % 239);
            Require(pixels[at] == b && pixels[at + 1] == g && pixels[at + 2] == r && pixels[at + 3] == 255, $"decoded crop pixel differs at {x},{y}");
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
