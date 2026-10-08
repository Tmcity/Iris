using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.Core.Settings;
using Shouldly;
using static Iris.App.Tests.Settings.NetworkSettingsUiScope;

namespace Iris.App.Tests.Settings;

[Collection("ScreenshotRendering")]
public sealed class NetworkSettingsRenderingTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [Fact]
    public async Task CompleteNetworkPageAndLifecycleStates_RenderInBothThemesAndSizes()
    {
        string directory = Environment.GetEnvironmentVariable("IRIS_T036C_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "iris-t036c-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var manifest = new List<object>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        foreach (int width in new[] { 600, 760 })
        {
            await Run(async f =>
            {
                Prepare(f, theme, width); f.Select();
                f.Vm.Network.Mode.Value = ProxyMode.Custom;
                f.Vm.Network.Url.Value = "http://proxy.代理节点.long-label-for-review.example.test:8080/";
                f.Vm.Network.Credentials.Value = CredentialEditKind.Replace; f.Vm.Network.UserName.Value = "synthetic-user 用户名";
                ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password = "synthetic-masked-password";
                var scroll = (ScrollViewer)f.Window.FindName("NetworkPageScroll");
                scroll.ScrollToTop(); await Render(f, $"{theme}-{width}-fields-top", directory, manifest);
                ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).BringIntoView(); await Render(f, $"{theme}-{width}-credentials", directory, manifest);
                scroll.ScrollToEnd(); await Render(f, $"{theme}-{width}-target", directory, manifest);
            }, LongInitial());

            foreach (string state in new[] { "empty", "invalid", "running", "ending", "transport-failed", "cleanup-failed", "readonly", "apply-pending", "http-response" })
            {
                var initial = LongInitial();
                if (state == "empty") initial = initial with { Translation = initial.Translation with { Providers = [] }, Ocr = initial.Ocr with
                    { Providers = [initial.Ocr.Providers[0] with { Id = BuiltInProviderIds.BaiduOcr, BaiduOptions = new() { BaseUrl = "invalid saved endpoint" } }, initial.Ocr.Providers[1]] } };
                await Run(async f =>
                {
                    Prepare(f, theme, width); var scroll = (ScrollViewer)f.Window.FindName("NetworkPageScroll");
                    if (state is not "empty" and not "readonly") f.Select();
                    if (state == "empty") { f.Vm.NetworkEndpoints.ShouldBeEmpty(); f.Vm.NetworkEndpointHint.ShouldBe(UiText.NetworkNoEndpoints); }
                    if (state == "invalid")
                    {
                        f.Vm.Network.Mode.Value = ProxyMode.Custom; f.Vm.Network.Url.Value = "current invalid address";
                        await f.Vm.ExecuteSaveAsync(); f.Vm.Network.ErrorMessage.ShouldNotBeNull();
                    }
                    if (state is "running" or "ending")
                    {
                        var gate = f.NewGate(); f.Transport.Send = async (_, _) => { await gate.PauseAsync(); return new(HttpStatusCode.OK); };
                        var work = f.Track(f.Vm.ExecuteNetworkTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (state == "ending") { f.Vm.CancelNetworkTest(); f.Vm.NetworkResultText.ShouldBeNull(); }
                        scroll.ScrollToEnd(); await Render(f, $"{theme}-{width}-{state}", directory, manifest);
                        gate.Open(); await work;
                        if (state == "ending") f.Vm.NetworkResultText.ShouldBe(UiText.NetworkCanceled);
                        return;
                    }
                    if (state == "transport-failed")
                    {
                        f.Transport.Send = (_, _) => throw new HttpRequestException("synthetic private transport detail");
                        await f.Vm.ExecuteNetworkTestAsync(); f.Vm.NetworkResultText.ShouldBe(UiText.NetworkTransportFailed);
                    }
                    if (state == "cleanup-failed")
                    {
                        var content = new Content { Fail = true }; f.Restore.Add(() => content.Fail = false);
                        f.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                        await f.Vm.ExecuteNetworkTestAsync(); f.Vm.HasFailedNetworkCleanup.ShouldBeTrue();
                    }
                    if (state == "apply-pending")
                    {
                        f.Vm.Network.Mode.Value = ProxyMode.System; f.NetworkObserver.Fail = true;
                        await f.Vm.ExecuteSaveAsync(); f.Receipts.PendingCount.ShouldBe(1);
                    }
                    if (state == "http-response")
                    {
                        f.Wire.Respond = (_, _) => "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n";
                        await f.Vm.ExecuteNetworkTestAsync(); f.Vm.NetworkResultText!.ShouldContain("HTTP 401");
                        f.Vm.NetworkResultText!.ShouldContain(UiText.NetworkDirect);
                    }
                    if (state == "readonly") { f.Vm.IsReadOnly.ShouldBeTrue(); f.Vm.TestNetworkCommand.CanExecute(null).ShouldBeFalse(); }
                    scroll.ScrollToEnd(); await Render(f, $"{theme}-{width}-{state}", directory, manifest);
                }, initial, readOnly: state == "readonly");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Json), TestContext.Current.CancellationToken);
        Console.WriteLine($"NETWORK_VISUAL files={manifest.Count} directory={directory}");
    }

    private static AppSettings LongInitial()
    {
        var initial = Initial(); var profile = initial.Translation.Providers[0];
        return initial with { Translation = initial.Translation with { Providers = [profile with
        { DisplayName = "中文服务与 English endpoint with a long descriptive label for configuration review", BaseUrl = "http://translation.example.test/long/service/路径/endpoint/for/network/connection/review" }] } };
    }
    private static void Prepare(NetworkSettingsUiScope f, AppTheme theme, int width)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme == AppTheme.Dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        f.Window.Width = width; f.Window.Height = width == 600 ? 440 : 520; f.Show();
    }
    private static async Task Render(NetworkSettingsUiScope f, string name, string directory, List<object> manifest)
    {
        f.Window.UpdateLayout(); await Frame(f.Window); await Frame(f.Window);
        await f.Window.Dispatcher.InvokeAsync(f.Window.UpdateLayout, DispatcherPriority.ContextIdle);
        var size = new Size(Math.Ceiling(f.Window.ActualWidth), Math.Ceiling(f.Window.ActualHeight));
        foreach (var button in Descendants<Button>(f.Window).Where(b => b.IsVisible && b.ActualWidth > 0))
        {
            var edge = button.TransformToAncestor(f.Window).Transform(new Point(button.ActualWidth, 0));
            edge.X.ShouldBeLessThanOrEqualTo(size.Width);
        }
        var frame = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); frame.Render(f.Window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        string path = Path.Combine(directory, name + ".png"); using (var output = File.Create(path)) encoder.Save(output);
        manifest.Add(new { File = Path.GetFileName(path), Width = size.Width, Height = size.Height, Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) });
    }
    private static async Task Frame(FrameworkElement element)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Tick(object? sender, EventArgs args) => ready.TrySetResult();
        CompositionTarget.Rendering += Tick;
        try { element.InvalidateVisual(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Tick; }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
