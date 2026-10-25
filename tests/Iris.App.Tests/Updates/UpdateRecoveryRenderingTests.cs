using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Resources;
using Iris.App.Tests.TestSupport;
using Iris.App.Updates;
using Shouldly;
using Wpf.Ui.Appearance;

namespace Iris.App.Tests.Updates;

[Collection("Localization")]
public sealed class UpdateRecoveryRenderingTests
{
    private static readonly JsonSerializerOptions FrameJson = new() { WriteIndented = true };

    [Theory]
    [InlineData("zh-CN", 540, false)]
    [InlineData("en-US", 540, false)]
    [InlineData("zh-CN", 380, false)]
    [InlineData("en-US", 380, false)]
    [InlineData("zh-CN", 540, true)]
    [InlineData("en-US", 540, true)]
    [InlineData("zh-CN", 380, true)]
    [InlineData("en-US", 380, true)]
    public async Task DedicatedRecoveryShowsCurrentLanguageAndExecutableActions(string language, int width, bool dark)
    {
        // One stable language run per theory case, never switch language within it.
        using var run = AppLanguage.BeginRun();
        AppLanguage.Select(language);
        await WpfTestRunner.RunAsync(async () =>
        {
            var app = Application.Current;
            var originalResources = app.Resources;
            var originalTheme = ApplicationThemeManager.GetAppTheme();
            // Exactly App.xaml's global resources. No per-window dictionaries,
            // foreground or background overrides are installed by this test.
            app.Resources = new ResourceDictionary();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme);
            int retries = 0, cancellations = 0;
            var window = new UpdateRecoveryWindow(() => { retries++; return Task.CompletedTask; }, () => { cancellations++; return Task.CompletedTask; }) { Width = width };
            try
            {
                AppLanguage.Name.ShouldBe(language);
                ApplicationThemeManager.GetAppTheme().ShouldBe(theme);
                window.ShowFailure("1.1.0", cleanupPending: true);
                window.FailureText.ShouldContain(UiText.UpdateRecoveryRequired);
                UiText.UpdateRecoveryRetry.ShouldBe(language == "zh-CN" ? "重试安装" : "Retry installation");
                UiText.UpdateRecoveryCancel.ShouldBe(language == "zh-CN" ? "取消更新并退出" : "Cancel update and exit");
                window.RetryCommand.Execute(null); await window.RetryCommand.ExecutionTask!;
                window.CancelUpdateCommand.Execute(null); await window.CancelUpdateCommand.ExecutionTask!;
                retries.ShouldBe(1); cancellations.ShouldBe(1);
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var panel = (StackPanel)window.Content;
                var text = panel.Children.OfType<TextBlock>().ToArray();
                var actions = panel.Children.OfType<WrapPanel>().Single();
                var buttons = actions.Children.OfType<Button>().ToArray();
                var background = Paint(window.Background, Colors.White);
                var bodyContrast = text.Select(item => Contrast(Paint(item.Foreground, background), background)).ToArray();
                var buttonPaint = buttons.Select(button =>
                {
                    var fill = Descendants<Border>(button).FirstOrDefault(border => border.Background != null && border.ActualWidth >= button.ActualWidth - 2)?.Background
                        ?? button.Background;
                    var surface = Paint(fill, background);
                    var label = Descendants<TextBlock>(button).FirstOrDefault(item => item.Text == button.Content?.ToString());
                    var foreground = Paint(label?.Foreground ?? button.Foreground, surface);
                    return new { Label = button.Content?.ToString(), Foreground = foreground.ToString(CultureInfo.InvariantCulture), Background = surface.ToString(CultureInfo.InvariantCulture), Contrast = Contrast(foreground, surface) };
                }).ToArray();
                string? output = Environment.GetEnvironmentVariable("IRIS_R4_EVIDENCE");
                if (output != null)
                {
                    Directory.CreateDirectory(output);
                    string prefix = "recovery-" + language + "-" + theme + "-" + width;
                    string path = Path.Combine(output, prefix + ".png");
                    var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    using (var stream = File.Create(path))
                    {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
                    }
                    File.WriteAllText(Path.Combine(output, prefix + ".json"), JsonSerializer.Serialize(new
                    {
                        RequestedLanguage = language, ActualLanguage = AppLanguage.Name, RequestedTheme = theme.ToString(),
                        ActualTheme = ApplicationThemeManager.GetAppTheme().ToString(), RequestedWidth = width, window.ActualWidth,
                        window.ActualHeight, WindowBackground = background.ToString(CultureInfo.InvariantCulture), BodyContrast = bodyContrast, Buttons = buttonPaint,
                        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                        AppDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(UpdateRecoveryWindow).Assembly.Location))),
                    }, FrameJson));
                }
                foreach (var item in text) item.ActualHeight.ShouldBeGreaterThan(0);
                foreach (var button in buttons) button.ActualWidth.ShouldBeLessThanOrEqualTo(panel.ActualWidth);
                foreach (double ratio in bodyContrast) ratio.ShouldBeGreaterThanOrEqualTo(4.5);
                foreach (var button in buttonPaint)
                    button.Contrast.ShouldBeGreaterThanOrEqualTo(4.5, "the actual action label must be readable against its painted surface");
                window.SetBusy(true);
                buttons.ShouldAllBe(button => !button.IsEnabled);
                window.SetBusy(false);
                buttons.ShouldAllBe(button => button.IsEnabled);
            }
            finally
            {
                window.Close();
                app.Resources = originalResources;
                ApplicationThemeManager.Apply(originalTheme);
            }
        });
    }

    private static Color Paint(Brush? brush, Color background)
    {
        if (brush == null) return background;
        var solid = brush.ShouldBeOfType<SolidColorBrush>();
        double alpha = solid.Color.A / 255d * solid.Opacity;
        return Color.FromRgb((byte)Math.Round(solid.Color.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(solid.Color.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(solid.Color.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(byte value) { double channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double first = Luminance(foreground), second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
