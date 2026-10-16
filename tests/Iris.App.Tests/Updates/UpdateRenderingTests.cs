using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Iris.App.Resources;
using Iris.App.Settings;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Updates;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Updates;

[Collection("Localization")]
public sealed class UpdateRenderingTests
{
    [Fact]
    public async Task ActualUpdateCommandsAndBilingualStatesRenderInBothThemesAndSizes()
    {
        string output = Environment.GetEnvironmentVariable("IRIS_T039B_UI") ?? Path.Combine(Path.GetTempPath(), "iris-t039b-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output); var manifest = new List<object>();
        foreach (string language in new[] { "zh-CN", "en-US" })
        foreach (bool dark in new[] { false, true })
        foreach (bool small in new[] { false, true })
        {
            using var culture = AppLanguage.BeginRun(); AppLanguage.Select(language);
            using var temp = new TempDirectory();
            var source = new UpdateCoordinatorTests.Source { Package = UpdateCoordinatorTests.PackageBytes(), Hold = true };
            var runtime = new UpdateCoordinatorTests.Runtime(temp.Path, source);
            string prefix = $"{language}-{(dark ? "dark" : "light")}-{(small ? "small" : "normal")}";
            var initial = NetworkSettingsUiScope.Initial(); initial = initial with { General = initial.General with { CheckForUpdates = false, UiLanguage = language } };
            await NetworkSettingsUiScope.Run(async f =>
            {
                var coordinator = f.Services.GetRequiredService<AppUpdateCoordinator>();
                var callbacks = new ConcurrentQueue<Task>();
                void Track(Task task) { callbacks.Enqueue(task); f.Track(task); }
                f.Ui.InvocationStarted += Track;
                async Task Drain() { while (callbacks.TryDequeue(out var task)) await task; f.Window.UpdateLayout(); }
                Task? command = null;
                try
                {
                    f.Window.Width = small ? 600 : 760; f.Window.Height = small ? 440 : 520;
                    var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                    f.Window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
                    f.Window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                    f.Window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
                    f.Show(); f.Vm.CurrentPageKey = "general";
                    coordinator.Start(); await Drain();
                    f.Vm.CheckUpdateCommand.CanExecute(null).ShouldBeTrue();
                    await Capture(f.Window, prefix + "-idle", output, manifest);
                    var check = (AsyncRelayCommand)f.Vm.CheckUpdateCommand; check.Execute(null); command = check.ExecutionTask!;
                    await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); await Drain();
                    f.Vm.UpdateStatus.ShouldBe(UiText.UpdateChecking); f.Vm.CancelUpdateCommand.CanExecute(null).ShouldBeTrue();
                    await Capture(f.Window, prefix + "-checking", output, manifest);
                    source.Release.TrySetResult(); await command; await Drain();
                    f.Vm.UpdateStatus.ShouldBe(UiText.UpdateAvailable); f.Vm.DownloadUpdateCommand.CanExecute(null).ShouldBeTrue();
                    await Capture(f.Window, prefix + "-available", output, manifest);
                    var download = (AsyncRelayCommand)f.Vm.DownloadUpdateCommand; download.Execute(null); command = download.ExecutionTask!;
                    await command; await Drain();
                    f.Vm.UpdateStatus.ShouldBe(UiText.UpdateReady); f.Vm.InstallUpdateCommand.CanExecute(null).ShouldBeTrue();
                    await Capture(f.Window, prefix + "-ready", output, manifest);
                    f.Updates.ShouldBe(0); f.Settings.Current.General.CheckForUpdates.ShouldBeFalse();
                }
                finally
                {
                    source.Release.TrySetResult(); if (command != null) await command;
                    await coordinator.StopAsync(); await Drain(); f.Ui.InvocationStarted -= Track;
                }
            }, initial, configure: services =>
            {
                services.AddSingleton<UpdateRuntime>(runtime); services.AddSingleton<AppUpdateCoordinator>();
                services.AddSingleton(new UpdateHandoff()); services.AddSingleton(Substitute.For<IAppLifetime>());
            });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
        Console.WriteLine($"UPDATE_UI images={manifest.Count} directory={output}");
    }

    private static async Task Capture(SettingsWindow window, string name, string output, List<object> manifest)
    {
        var card = Descendants<FrameworkElement>(window).Single(value => value.Name == "CheckForUpdatesCheckBox").Parent;
        var viewer = Descendants<ScrollViewer>(window).First(value => value.IsVisible && value.Name == "GeneralPageScroll");
        ((FrameworkElement)card).BringIntoView(); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        for (int part = 0; part < 2; part++)
        {
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(window);
            string file = name + "-" + part + ".png"; string path = Path.Combine(output, file);
            using (var stream = File.Create(path)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); }
            manifest.Add(new { File = file, Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))) });
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + viewer.ViewportHeight * .65); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var next in Descendants<T>(child)) yield return next;
        }
    }
}
