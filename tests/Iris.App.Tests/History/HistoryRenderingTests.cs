using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Settings;
using Iris.App.Tests.Settings;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using static Iris.App.Tests.History.HistoryTestScope;

namespace Iris.App.Tests.History;

[Collection("ScreenshotRendering")]
public sealed class HistoryRenderingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [Fact]
    public async Task ActualHistoryAndSettingsWindows_RenderBothThemesAndWorkAreas()
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("IRIS_T035_EVIDENCE") ?? Path.GetTempPath(), "history-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var manifest = new List<object>();
        foreach (var dark in new[] { false, true })
        {
            foreach (var size in new[] { new Size(800, 640), new Size(320, 280) })
            {
                await Run(async f =>
                {
                    f.Desktop.GetMonitors().Returns([new("synthetic", new(0, 0, (int)size.Width, (int)size.Height), new(0, 0, (int)size.Width, (int)size.Height), 1, true)]);
                    f.ConfigureWindow = window => Theme(window, dark);
                    await f.ShowAsync(); var window = f.Window; var vm = f.Vm;
                    window.UpdateLayout();
                    string prefix = $"{(dark ? "Dark" : "Light")}-{size.Width}";
                    Task Capture(string state) => Render(window, $"{prefix}-{state}", directory, manifest);
                    var scroll = (ScrollViewer)window.FindName("HistoryScroll");
                    await Capture("empty");
                    for (int i = 0; i < 51; i++) await f.Repository.Inner.AddAsync(Entry($"{i}: 中文历史记录与 English source for wrapping.\n第二行与长英文 {new string('a', 210)}", i % 2 == 0));
                    await vm.RefreshAsync(); await Capture("list-pagination");
                    ((Button)window.FindName("PreviousButton")).BringIntoView(); await Capture("page-navigation");
                    await f.SelectAsync(); ((Button)window.FindName("RetranslateButton")).BringIntoView(); await Capture("row-actions");
                    ((TextBox)window.FindName("SourceTextBox")).BringIntoView(); await Capture("source-detail");
                    window.UpdateLayout();
                    var firstResult = (FrameworkElement)((ItemsControl)window.FindName("ResultsList")).ItemContainerGenerator.ContainerFromIndex(0);
                    firstResult.BringIntoView(new Rect(0, 0, firstResult.ActualWidth, Math.Min(80, firstResult.ActualHeight))); await Capture("first-result");
                    var lastResult = (FrameworkElement)((ItemsControl)window.FindName("ResultsList")).ItemContainerGenerator.ContainerFromIndex(1);
                    lastResult.BringIntoView(new Rect(0, 0, lastResult.ActualWidth, Math.Min(80, lastResult.ActualHeight))); await Capture("last-result-header");
                    scroll.ScrollToEnd(); await Capture("results-truncated");
                    vm.RequestDelete(); ((Border)window.FindName("ConfirmationPanel")).BringIntoView(); await Capture("confirm-delete");
                    vm.CancelConfirmation(); vm.RequestClear(); await Capture("confirm-clear"); vm.CancelConfirmation();
                    var gate = f.NewGate(); f.Repository.QueryOverride = async (q, ct) => { await gate.Pause(); return await f.Repository.Inner.QueryAsync(q, ct); };
                    var query = f.Track(vm.RefreshAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    scroll.ScrollToTop(); await Capture("loading"); gate.Open(); await query;
                    f.Repository.QueryOverride = (_, _) => throw new IOException("synthetic private");
                    await vm.RefreshAsync(); await Capture("error");
                    f.Repository.QueryOverride = null; await vm.RefreshAsync(); await f.SelectAsync();
                    var writeGate = f.NewGate(); f.Repository.BeforeWrite = writeGate.Pause;
                    var write = f.Track(vm.ToggleFavoriteAsync()); await writeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Capture("writing"); writeGate.Open(); await write; await vm.QueryTask;
                    static void Reject(object? sender, CancelEventArgs args) => args.Cancel = true;
                    window.Closing += Reject; f.Undo.Add(() => window.Closing -= Reject);
                    await Should.ThrowAsync<InvalidOperationException>(() => f.Session.ShutdownAsync());
                    await Capture("close-retry"); window.Closing -= Reject;
                });
            }
            foreach (var size in new[] { new Size(600, 440), new Size(760, 520) })
            {
                await OcrSettingsUiScope.Run(async f =>
                {
                    Theme(f.Window, dark); f.Window.Width = size.Width; f.Window.Height = size.Height; f.Show("history");
                    string prefix = $"{(dark ? "Dark" : "Light")}-{size.Width}-settings";
                    await Render(f.Window, prefix, directory, manifest);
                    f.Vm.History.MaxEntriesText.Value = "不是整数"; await f.Vm.ExecuteSaveAsync();
                    await Render(f.Window, prefix + "-invalid", directory, manifest);
                });
            }
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    }
    private static void Theme(Window window, bool dark)
    {
        // This nonparallel collection also resets WPF-UI's global accent palette before loading local theme resources.
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        { Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light });
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
    }
    private static async Task Render(Window window, string name, string directory, List<object> manifest)
    {
        window.UpdateLayout(); await Frame(window); await Frame(window);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
        int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
        var close = window.FindName("CloseButton") as FrameworkElement;
        if (close != null)
        {
            var edge = close.TransformToAncestor(window).Transform(new Point(close.ActualWidth, close.ActualHeight));
            edge.X.ShouldBeLessThanOrEqualTo(width); edge.Y.ShouldBeLessThanOrEqualTo(height);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream);
        manifest.Add(new { File = Path.GetFileName(path), Width = width, Height = height, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
    }
    private static async Task Frame(FrameworkElement element)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, EventArgs args) => entered.TrySetResult();
        CompositionTarget.Rendering += OnRendering;
        try { element.InvalidateVisual(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }
}
