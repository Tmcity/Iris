using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Ocr;

[Collection("AppShutdownerSharedState")]
public sealed class OcrRenderingTests
{
    [Theory][InlineData(AppTheme.Light)][InlineData(AppTheme.Dark)]
    public Task ActualOcrEntrypoint_RendersLoadingSourceAndErrorInSameWindowWithoutImage(AppTheme theme) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        s.Settings.Current.Returns(s.Settings.Current with { General = new() { Theme = theme } });
        using var themes = new ThemeService(s.Settings); themes.Apply(theme);
        var dir = Path.Combine(Environment.GetEnvironmentVariable("IRIS_T031B_EVIDENCE") ?? Path.GetTempPath(), "ocr-window-" + theme + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // This is an owned synthetic red image. It must remain at the capture/OCR boundary.
        byte[] pixels = new byte[40 * 40 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(40, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 160);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); png.Save(stream);
        var image = new ImageData(stream.ToArray(), ImageFormat.Png, 40, 40);
        s.Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns(new ScreenshotResult(image, new(100, 100, 40, 40)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OcrOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ocr.RecognizeAsync(image, null, Arg.Any<CancellationToken>()).Returns(_ => { entered.TrySetResult(); return release.Task; });
        var work = s.Run(IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var window = s.Presenter.Window!;
            s.Presenter.ViewModel!.StatusMessage.ShouldBe("正在识别文字…");
            Render(window, Path.Combine(dir, "loading.png"));
            release.SetResult(OcrEntrypointScope.Outcome("OCR 识别原文\nHello from a synthetic image.\n可编辑，按需翻译。"));
            await work;
            s.Presenter.Window.ShouldBeSameAs(window); s.Translations.ShouldBeEmpty();
            ((TextBox)window.FindName("InputTextBox")).Text.ShouldBe(s.Presenter.ViewModel.InputText);
            Render(window, Path.Combine(dir, "source.png"));
            s.Ocr.RecognizeAsync(image, null, Arg.Any<CancellationToken>()).Returns<Task<OcrOutcome>>(_ => throw new IrisException(ErrorCode.OcrNoTextFound, "private error"));
            await s.Run(IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
            s.Presenter.Window.ShouldBeSameAs(window);
            s.Presenter.ViewModel.Error!.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.OcrNoTextFound));
            s.Presenter.ViewModel.InputText.ShouldStartWith("OCR 识别原文");
            Render(window, Path.Combine(dir, "error.png"));
            File.WriteAllText(Path.Combine(dir, "verification.txt"), "Actual Dispatcher/Handler/TranslateWindow/VM; external screenshot/OCR fakes; synthetic red PNG never displayed; no translation requests; loading/source/error share the same window.");
        }
        finally { release.TrySetResult(OcrEntrypointScope.Outcome()); await work; }
    });

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        Descendants(root).OfType<Image>().ShouldBeEmpty();
        root.ActualWidth.ShouldBeGreaterThan(0); root.ActualHeight.ShouldBeGreaterThan(0);
        var render = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        render.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
