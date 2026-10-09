using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Capture;
using Iris.App.Resources;
using Iris.App.Tests.Capture;
using Iris.App.Tests.History;
using Iris.App.Tests.Ocr;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Localization;

[Collection("Localization")]
public sealed class LocalizationRenderingTests
{
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };
    [Fact]
    public async Task CompleteBilingualUiRendersRealWindowsInBothThemesAndWorkAreas()
    {
        string directory = Environment.GetEnvironmentVariable("IRIS_T038_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "iris-t038-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var manifest = new List<object>();
        foreach (string language in new[] { "zh-CN", "en-US" })
        {
            using var run = AppLanguage.BeginRun(); AppLanguage.Select(language);
            foreach (bool dark in new[] { false, true })
            foreach (bool small in new[] { false, true })
            {
                string prefix = $"{language}-{(dark ? "dark" : "light")}-{(small ? "small" : "normal")}";
                await Settings(prefix, dark, small, directory, manifest, language);
                await Translation(prefix, dark, small, directory, manifest);
                await History(prefix, dark, small, directory, manifest);
                await Screenshot(prefix, dark, small, directory, manifest);
                await MenusAndNotifications(prefix, dark, small, directory, manifest);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestJson), TestContext.Current.CancellationToken);
        Console.WriteLine($"LOCALIZATION_VISUAL files={manifest.Count} directory={directory}");
    }

    private static Task Settings(string prefix, bool dark, bool small, string directory, List<object> manifest, string language)
    {
        var initial = NetworkSettingsUiScope.Initial();
        initial = initial with { General = initial.General with { UiLanguage = language } };
        return NetworkSettingsUiScope.Run(async f =>
        {
            Theme(f.Window, dark); f.Window.Width = small ? 600 : 760; f.Window.Height = small ? 440 : 520;
            f.Show();
            f.Vm.Network.Credentials.Value = CredentialEditKind.Replace;
            f.Vm.Network.UserName.Value = "synthetic-user"; f.Vm.Network.SetPassword("synthetic-masked-password");
            foreach (string page in new[] { "general", "translation", "hotkeys", "selection", "ocr", "screenshot", "network", "history" })
            {
                f.Vm.CurrentPageKey = page; f.Window.UpdateLayout(); await Frame(f.Window);
                string navName = page switch { "general" => "NavGeneral", "translation" => "NavTranslation", "hotkeys" => "NavHotkeys", "selection" => "NavSelection", "ocr" => "NavOcr", "screenshot" => "NavScreenshot", "network" => "NavNetwork", _ => "NavHistory" };
                (f.Window.FindName(navName) as FrameworkElement)?.BringIntoView();
                foreach (var expander in Descendants<Expander>(f.Window).Where(value => value.IsVisible)) expander.IsExpanded = true;
                if (page == "translation") await Task.Delay(350);
                await ScrollCaptures(f.Window, $"{prefix}-settings-{page}", directory, manifest);
                if (page == "translation")
                {
                    var button = (Button)f.Window.FindName("AddProfileButton");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    try
                    {
                        await CaptureMenu(button.ContextMenu!, prefix + "-preset-menu", directory, manifest);
                    }
                    finally { button.ContextMenu!.IsOpen = false; }
                }
            }
            f.Vm.CurrentPageKey = "translation";
            f.Vm.AddProfileFromPreset(Iris.App.Settings.SettingsViewModel.AvailablePresets.Single(preset => preset.Kind == TranslationProviderKind.Anthropic));
            f.Vm.SelectedProfile!.Model = "claude-haiku-synthetic";
            f.Vm.SelectedProfile.Effort = "low";
            f.Window.UpdateLayout(); await Frame(f.Window);
            foreach (var expander in Descendants<Expander>(f.Window).Where(value => value.IsVisible)) expander.IsExpanded = true;
            await Task.Delay(350);
            await ScrollCaptures(f.Window, prefix + "-settings-anthropic", directory, manifest);
        }, initial);
    }

    private static Task Translation(string prefix, bool dark, bool small, string directory, List<object> manifest) => WpfTestRunner.RunAsync(async () =>
    {
        await using var scope = new OcrEntrypointScope(window => Theme(window, dark));
        using var operation = scope.Presenter.TryBeginOperation();
        await scope.Presenter.TryShowAsync(operation!, new TranslateWindowRequest { Text = "Synthetic source text. 用户原文保持不变。", AutoTranslate = false });
        var window = scope.Presenter.Window!; var vm = scope.Presenter.ViewModel!;
        window.Width = small ? 280 : 520; window.Height = small ? 300 : 600;
        await Capture(window, prefix + "-translation-input", directory, manifest);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Translation.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>()).Returns(call => Events(entered, release.Task, call.Arg<CancellationToken>()));
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        { if (args.PropertyName == nameof(vm.IsTranslating) && !vm.IsTranslating) complete.TrySetResult(); };
        vm.PropertyChanged += changed;
        try
        {
            vm.ExecuteTranslateFromUser(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ScrollCaptures(window, prefix + "-translation-running", directory, manifest);
            release.TrySetResult(); await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Cards.Count.ShouldBe(1); vm.Cards[0].Text.ShouldBe("Synthetic translated result. 合成译文。\nSecond line.");
            await ScrollCaptures(window, prefix + "-translation-result", directory, manifest);
            if (small)
            {
                var cards = (ScrollViewer)window.FindName("CardsScrollViewer");
                cards.ViewportHeight.ShouldBeGreaterThanOrEqualTo(160);
                var copy = Descendants<Button>(cards).Single(button => Equals(button.Content, UiText.TranslateCopy));
                copy.BringIntoView(); await Frame(window);
                var center = copy.TranslatePoint(new Point(copy.ActualWidth / 2, copy.ActualHeight / 2), window);
                // The synthetic presenter lease can disable commands; verify visible geometry without invoking clipboard input.
                var hit = VisualTreeHelper.HitTest(window, center)?.VisualHit;
                while (hit != null && !ReferenceEquals(hit, copy)) hit = VisualTreeHelper.GetParent(hit);
                hit.ShouldBeSameAs(copy);
            }
            using var errorOperation = scope.Presenter.TryBeginOperation();
            await scope.Presenter.TryShowAsync(errorOperation!, new TranslateWindowRequest
            { Text = vm.InputText, AutoTranslate = false, Error = new(ErrorCode.AuthenticationFailed, ErrorMessages.GetDefault(ErrorCode.AuthenticationFailed)) });
            await ScrollCaptures(window, prefix + "-translation-error", directory, manifest);
        }
        finally { release.TrySetResult(); vm.PropertyChanged -= changed; }
    });

    private static async IAsyncEnumerable<TranslationEvent> Events(TaskCompletionSource entered, Task release, [EnumeratorCancellation] CancellationToken token)
    {
        var id = Guid.NewGuid();
        yield return new TranslationStarted(id, new("en", "zh-Hans", false), [new ProviderDescriptor("synthetic", "Synthetic provider with a long display name", "OpenAICompatible", "synthetic-model", true)]);
        yield return new ProviderStarted(id, "synthetic");
        entered.TrySetResult(); await release; token.ThrowIfCancellationRequested();
        yield return new ProviderCompleted(id, "synthetic", "Synthetic translated result. 合成译文。\nSecond line.", TimeSpan.FromMilliseconds(125), true, true, null);
        yield return new TranslationFinished(id, 1, 0);
    }

    private static Task History(string prefix, bool dark, bool small, string directory, List<object> manifest) => HistoryTestScope.Run(async f =>
    {
        int width = small ? 320 : 800, height = small ? 280 : 640;
        f.Desktop.GetMonitors().Returns([new("synthetic", new(0, 0, width, height), new(0, 0, width, height), 1, true)]);
        f.ConfigureWindow = window => Theme(window, dark);
        await f.ShowAsync(); await ScrollCaptures(f.Window, prefix + "-history-empty", directory, manifest);
        await f.Repository.Inner.AddAsync(HistoryTestScope.Entry("Synthetic history source. 历史原文保持不变。", true));
        await f.Vm.RefreshAsync(); await ScrollCaptures(f.Window, prefix + "-history-list", directory, manifest);
        await f.SelectAsync(); await ScrollCaptures(f.Window, prefix + "-history-detail", directory, manifest);
    });

    private static Task Screenshot(string prefix, bool dark, bool small, string directory, List<object> manifest) => ScreenshotTestScope.Run(async f =>
    {
        int width = small ? 320 : 640, height = small ? 180 : 360;
        var bounds = new PixelRect(-240, -120, width, height);
        f.Monitors = [new("synthetic", bounds, bounds, 1, true)];
        await f.Settings(new() { ConfirmOnMouseUp = false, ShowMagnifier = false, MaskOpacity = .45 }, dark ? AppTheme.Dark : AppTheme.Light);
        var work = f.Start(); await f.Ready();
        var window = f.Service.Windows.Single(); window.Width = width; window.Height = height;
        try
        {
            await Capture(window, prefix + "-screenshot-drag", directory, manifest);
            f.Select(bounds.X + 30, bounds.Y + 50, bounds.X + width - 30, bounds.Y + height - 30);
            await Capture(window, prefix + "-screenshot-confirm", directory, manifest);
        }
        finally { f.Input(ScreenshotInput.Escape); await work; }
    });

    private static Task MenusAndNotifications(string prefix, bool dark, bool small, string directory, List<object> manifest) => WpfTestRunner.RunAsync(async () =>
    {
        await using var scope = new OcrEntrypointScope();
        var owner = new Window { MinWidth = 0, MinHeight = 0, Width = small ? 320 : 520, Height = small ? 320 : 420, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        Theme(owner, dark); owner.Show();
        var menu = (ContextMenu)typeof(TrayIconController).GetMethod("CreateContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scope.Tray, null)!;
        try
        {
            menu.PlacementTarget = owner; menu.MaxWidth = small ? 300 : 500; menu.IsOpen = true;
            await CaptureMenu(menu, prefix + "-tray-menu", directory, manifest);
            menu.IsOpen = false;
            scope.Notifications.TryNotifyOcrCopied(123).ShouldBeTrue(); scope.Notifications.TryNotifyOcrFallback().ShouldBeTrue();
            scope.Notifications.TryNotifyScreenshotCopyFailure().ShouldBeTrue();
            scope.Notifications.TryNotifyNow(NotificationLevel.Error, ErrorCode.SecretUnavailable).ShouldBeTrue();
            var panel = new StackPanel { Margin = new(12) };
            foreach (var message in scope.Native.Messages)
            {
                panel.Children.Add(new TextBlock { Text = message.Title, FontWeight = FontWeights.SemiBold, Margin = new(0, 6, 0, 3) });
                panel.Children.Add(new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
            }
            owner.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            await ScrollCaptures(owner, prefix + "-notification-content", directory, manifest);
        }
        finally { menu.IsOpen = false; owner.Close(); }
    });

    private static async Task CaptureMenu(ContextMenu menu, string name, string directory, List<object> manifest)
    {
        // Owned popups can be dismissed by native activation while awaiting their opening animation.
        // Reopen only the actual menu, with a bounded attempt count; never substitute a list of labels.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            menu.IsOpen = true;
            await Task.Delay(350);
            if (menu.IsOpen && PresentationSource.FromVisual(menu)?.RootVisual is FrameworkElement root)
            {
                await Capture(root, name, directory, manifest);
                return;
            }
        }
        throw new InvalidOperationException("Owned popup did not remain open for visual capture.");
    }

    private static async Task ScrollCaptures(Window window, string name, string directory, List<object> manifest)
    {
        window.UpdateLayout(); await Frame(window);
        var scroll = Descendants<ScrollViewer>(window).Where(v => v.IsVisible && v.ViewportHeight > 60 && v.ActualWidth > window.ActualWidth * .25)
            .OrderByDescending(v => v.ScrollableHeight).FirstOrDefault();
        if (scroll == null || scroll.ScrollableHeight < 1) { await Capture(window, name + "-00", directory, manifest); return; }
        double offset = 0; int index = 0;
        while (true)
        {
            scroll.ScrollToVerticalOffset(offset); window.UpdateLayout(); await Frame(window);
            await Capture(window, name + "-" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture), directory, manifest);
            if (offset >= scroll.ScrollableHeight - 1) break;
            offset = Math.Min(scroll.ScrollableHeight, offset + Math.Max(60, scroll.ViewportHeight * .85)); index++;
            index.ShouldBeLessThan(24);
        }
    }
    private static void Theme(FrameworkElement element, bool dark)
    {
        var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
        element.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
        element.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        if (element is Window window) window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
    }
    private static async Task Capture(FrameworkElement element, string name, string directory, List<object> manifest)
    {
        element.UpdateLayout(); await Frame(element); await Frame(element);
        await element.Dispatcher.InvokeAsync(element.UpdateLayout, DispatcherPriority.ContextIdle);
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        width.ShouldBeGreaterThan(0); height.ShouldBeGreaterThan(0);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream);
        manifest.Add(new { File = Path.GetFileName(path), Width = width, Height = height, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            VisibleText = Descendants<TextBlock>(element).Where(t => t.IsVisible).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToArray(),
            Note = name.Contains("notification", StringComparison.Ordinal) ? "Actual notification content in an owned rendering host; native balloon appearance is not verified." : "Actual owned product window/control; synthetic user data may intentionally contain Chinese in either UI language." });
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
