using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Resources;
using Iris.App.Updates;
using Wpf.Ui.Appearance;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        const string expectedRoot = @"F:\Iris\artifacts\t039b\independent-r4-recovery-ui-2\output";
        if (args.Length != 2 || args[1] is not ("zh-CN" or "en-US")) throw new InvalidOperationException("An explicit owned language is required.");
        string language = args[1];
        string expected = Path.Combine(expectedRoot, language);
        if (args.Length != 2 || !Path.GetFullPath(args[0]).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only the explicitly owned review output is allowed.");
        if (Directory.Exists(expected) && Directory.EnumerateFileSystemEntries(expected).Any())
            throw new InvalidOperationException("Prior review output exists; use a separately bound new attempt.");
        Directory.CreateDirectory(expected);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Exactly the application resource composition declared in App.xaml.
        // Apply changes the real application theme; no window-specific brushes
        // or extra dictionaries are installed to hide an inheritance defect.
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        var frames = new List<object>();
        string? failure = null;
        int result = 1;
        app.Startup += async (_, _) =>
        {
            using var languageRun = AppLanguage.BeginRun();
            AppLanguage.Select(language);
            try
            {
                foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (int width in new[] { 540, 380 })
                {
                    if (AppLanguage.Name != language || Iris.App.UiText.UpdateRecoveryRetry != (language == "en-US" ? "Retry installation" : "重试安装"))
                        throw new InvalidOperationException("Fixture language did not match its label.");
                    ApplicationThemeManager.Apply(theme);
                    var window = new UpdateRecoveryWindow(() => Task.CompletedTask, () => Task.CompletedTask)
                    {
                        Width = width, ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                    };
                    try
                    {
                        window.ShowFailure("0.2.1", cleanupPending: true);
                        window.UpdateLayout();
                        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        string name = $"recovery-{language}-{theme}-{width}.png";
                        string file = Path.Combine(expected, name);
                        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                        }
                        frames.Add(new
                        {
                            language, RequestedTheme = theme.ToString(), ActualApplicationTheme = ApplicationThemeManager.GetAppTheme().ToString(),
                            RequestedWidth = width, ActualWidth = window.ActualWidth, ActualHeight = window.ActualHeight,
                            window.FailureText, File = name, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                        });
                    }
                    finally { window.Close(); }
                }
                result = 0;
            }
            catch (Exception error) { failure = error.ToString(); }
            finally
            {
                string manifest = JsonSerializer.Serialize(new
                {
                    Scope = "Rendering only; one language run per child process. No host, update, installation, network, production IPC or user settings are started.",
                    ActualLanguage = AppLanguage.Name, AppDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(UpdateRecoveryWindow).Assembly.Location))),
                    Frames = frames, Failure = failure,
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(expected, "render-result.json"), manifest);
                Console.WriteLine(manifest);
                app.Shutdown(result);
            }
        };
        app.Run();
        return result;
    }
}
