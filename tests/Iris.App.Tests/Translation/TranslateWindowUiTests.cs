using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Common;
using Iris.Core.Languages;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Translation;

[Collection("AppShutdownerSharedState")]
public sealed class TranslateWindowUiTests
{
    private static ServiceProvider CreateServiceProvider(AppSettings? initialSettings = null, TimeProvider? timeProvider = null)
    {
        var settings = initialSettings ?? new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var orchestrator = Substitute.For<ITranslationOrchestrator>();
        var clipboard = Substitute.For<IClipboardService>();
        var settingsPresenter = Substitute.For<ISettingsWindowPresenter>();
        var desktopContext = Substitute.For<IDesktopContext>();
        desktopContext.GetMonitors().Returns([
            new MonitorInfo(@"\\.\DISPLAY1", new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1080), new Iris.Core.Imaging.PixelRect(0, 0, 1920, 1040), 1.0, true)
        ]);
        desktopContext.GetCursorPosition().Returns(new Iris.Core.Imaging.PixelPoint(500, 500));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(settingsService);
        services.AddSingleton(orchestrator);
        services.AddSingleton(clipboard);
        services.AddSingleton(settingsPresenter);
        services.AddSingleton(desktopContext);
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ThemeService>();

        services.AddSingleton<TranslateWindowPresenter>();
        services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        services.AddSingleton<ITranslateWindowShutdownCoordinator>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        var textPreprocessor = Substitute.For<ITextPreprocessor>();
        textPreprocessor.Prepare(Arg.Any<string>(), Arg.Any<TextOrigin>())
            .Returns(ci => ci.Arg<string>()?.Trim() ?? string.Empty);
        services.AddSingleton(textPreprocessor);

        services.AddScoped<TranslateWindowViewModel>(sp => new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            clipboard,
            settingsPresenter,
            sp.GetRequiredService<IUiDispatcher>(),
            NullLogger<TranslateWindowViewModel>.Instance,
            textPreprocessor,
            sp.GetRequiredService<TimeProvider>()));

        services.AddTransient<TranslateWindow>();

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(1200, Wpf.Ui.Appearance.ApplicationTheme.Light, 480)]
    [InlineData(1200, Wpf.Ui.Appearance.ApplicationTheme.Dark, 480)]
    [InlineData(460, Wpf.Ui.Appearance.ApplicationTheme.Light, 480)]
    [InlineData(460, Wpf.Ui.Appearance.ApplicationTheme.Dark, 480)]
    [InlineData(280, Wpf.Ui.Appearance.ApplicationTheme.Light, 480)]
    [InlineData(280, Wpf.Ui.Appearance.ApplicationTheme.Dark, 480)]
    [InlineData(280, Wpf.Ui.Appearance.ApplicationTheme.Light, 300)]
    public async Task Layout_AtDifferentWidthsAndThemes_ControlsAreAccessibleAndNotClipped(double width, Wpf.Ui.Appearance.ApplicationTheme theme, double height)
    {
        await WpfTestRunner.RunAsync(() =>
        {
            var appTheme = theme == Wpf.Ui.Appearance.ApplicationTheme.Dark ? AppTheme.Dark : AppTheme.Light;
            var themeDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
            if (themeDict != null)
            {
                themeDict.Theme = theme;
            }
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);

            var initialSettings = new AppSettings
            {
                General = new GeneralSettings { Theme = appTheme },
                TranslateWindow = new TranslateWindowSettings { Width = width }
            };

            using var provider = CreateServiceProvider(initialSettings);
            using var scope = provider.CreateScope();
            var themeService = scope.ServiceProvider.GetRequiredService<ThemeService>();
            themeService.Apply(appTheme);

            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();

            try
            {
                window.DataContext = vm;
                window.Left = -20000;
                window.Top = -20000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Width = width;
                window.Height = height;
                window.Show();
                window.UpdateLayout();

                // 1. 验证实际主题资源与前景色/背景色对比度
                var textBrush = (window.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush)
                    ?? (System.Windows.Application.Current.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush);
                textBrush.ShouldNotBeNull();
                var bgBrush = (window.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush)
                    ?? (System.Windows.Application.Current.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush);
                bgBrush.ShouldNotBeNull();
                if (theme == Wpf.Ui.Appearance.ApplicationTheme.Dark)
                {
                    textBrush.Color.R.ShouldBeGreaterThan(bgBrush.Color.R, "暗色主题下前景色亮度高于背景色");
                }
                else
                {
                    bgBrush.Color.R.ShouldBeGreaterThan(textBrush.Color.R, "亮色主题下背景色亮度高于前景色");
                }

                // 2. 验证核心控件可达且具有有效尺寸
                window.FindName("TranslateButton").ShouldBeNull();
                window.FindName("CloseButton").ShouldBeNull();

                var inputTb = (TextBox)window.FindName("InputTextBox");
                var sourceCombo = (ComboBox)window.FindName("SourceLangComboBox");
                var swapBtn = (Wpf.Ui.Controls.Button)window.FindName("SwapLanguagesButton");
                var targetCombo = (ComboBox)window.FindName("TargetLangComboBox");
                var pinBtn = (Wpf.Ui.Controls.Button)window.FindName("PinButton");
                var settingsBtn = (Wpf.Ui.Controls.Button)window.FindName("SettingsButton");
                var scrollViewer = (ScrollViewer)window.FindName("CardsScrollViewer");

                inputTb.ShouldNotBeNull();
                sourceCombo.ShouldNotBeNull();
                swapBtn.ShouldNotBeNull();
                targetCombo.ShouldNotBeNull();
                pinBtn.ShouldNotBeNull();
                settingsBtn.ShouldNotBeNull();
                scrollViewer.ShouldNotBeNull();

                inputTb.ActualWidth.ShouldBeGreaterThan(100);
                sourceCombo.ActualWidth.ShouldBeGreaterThan(40);
                swapBtn.ActualWidth.ShouldBeGreaterThan(20);
                targetCombo.ActualWidth.ShouldBeGreaterThan(40);
                pinBtn.ActualWidth.ShouldBeGreaterThan(20);
                settingsBtn.ActualWidth.ShouldBeGreaterThan(20);

                foreach (var element in new FrameworkElement[] { sourceCombo, swapBtn, targetCombo, pinBtn, settingsBtn, inputTb })
                {
                    var rect = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                    rect.Left.ShouldBeGreaterThanOrEqualTo(0, $"{element.Name} left edge clipped");
                    rect.Right.ShouldBeLessThanOrEqualTo(window.ActualWidth + 0.5, $"{element.Name} right edge clipped");
                    rect.Top.ShouldBeGreaterThanOrEqualTo(0, $"{element.Name} top edge clipped");
                    rect.Bottom.ShouldBeLessThanOrEqualTo(window.ActualHeight + 0.5, $"{element.Name} bottom edge clipped");
                }

                if (height < 440)
                {
                    scrollViewer.MinHeight.ShouldBe(160);
                    var contentScrollViewer = (ScrollViewer)window.FindName("ContentScrollViewer");
                    contentScrollViewer.ShouldNotBeNull();
                    contentScrollViewer.ActualHeight.ShouldBeGreaterThan(0);
                }
            }
            finally
            {
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
                themeService.Apply(AppTheme.Light);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                var restoreDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
                if (restoreDict != null)
                {
                    restoreDict.Theme = Wpf.Ui.Appearance.ApplicationTheme.Light;
                }
            }
        });
    }

    [Fact]
    public async Task KeyHandling_ImeCompositionAndShiftEnter_DoNotTriggerTranslate()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var streamGate = new TaskCompletionSource();
            async IAsyncEnumerable<TranslationEvent> PendingStream()
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                await streamGate.Task;
            }

            using var provider = CreateServiceProvider();
            var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();
            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(PendingStream());

            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            using var hwndSource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "TestHwnd", IntPtr.Zero);
            var previousModifiers = TranslateWindow.TestKeyboardModifiersProvider;

            try
            {
                TranslateWindow.TestKeyboardModifiersProvider = () => ModifierKeys.None;
                window.DataContext = vm;
                window.AttachPresenter(presenter);
                vm.AttachPresenter(presenter);
                window.Show();
                vm.InputText = "Sample Text";
                window.UpdateLayout();

                var inputTb = (TextBox)window.FindName("InputTextBox");
                inputTb.ShouldNotBeNull();

                // 1. 模拟 IME 组合输入确认发往 InputTextBox，不触发翻译
                var imeEventArgs = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    hwndSource,
                    0,
                    Key.ImeProcessed)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };

                inputTb.RaiseEvent(imeEventArgs);
                vm.IsTranslating.ShouldBeFalse("IME 组合确认不应触发翻译");

                // 2. 模拟 Shift+Enter 发往 InputTextBox（多行换行），不触发翻译且不拦截
                TranslateWindow.TestKeyboardModifiersProvider = () => ModifierKeys.Shift;
                try
                {
                    var shiftEnterEventArgs = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        hwndSource,
                        0,
                        Key.Enter)
                    {
                        RoutedEvent = UIElement.PreviewKeyDownEvent
                    };

                    inputTb.RaiseEvent(shiftEnterEventArgs);
                    shiftEnterEventArgs.Handled.ShouldBeFalse("Shift+Enter 不应被翻译拦截，保留换行能力");
                    vm.IsTranslating.ShouldBeFalse("Shift+Enter 不应触发翻译");
                }
                finally
                {
                    TranslateWindow.TestKeyboardModifiersProvider = () => ModifierKeys.None;
                }

                // 3. 模拟普通 Enter 发往 InputTextBox，触发翻译并进入流式中
                var enterEventArgs = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    hwndSource,
                    0,
                    Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };

                inputTb.RaiseEvent(enterEventArgs);
                vm.IsTranslating.ShouldBeTrue("默认 EnterToTranslate=true 时 Enter 触发翻译");

                // 4. 放行流式任务闸门并等待收尾
                streamGate.SetResult();
                await vm.WaitForActiveWorkAsync();
                vm.IsTranslating.ShouldBeFalse();
            }
            finally
            {
                streamGate.TrySetResult();
                TranslateWindow.TestKeyboardModifiersProvider = previousModifiers;
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task OnWindowHidden_RestoresTranslateAndStopButtonVisibilityAndCanExecute()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();

            var orchestrator = scope.ServiceProvider.GetRequiredService<ITranslationOrchestrator>();
            var streamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async IAsyncEnumerable<TranslationEvent> HangingStream()
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                yield return new ProviderStarted(Guid.NewGuid(), "p1");
                await streamGate.Task;
                yield return new TranslationFinished(Guid.NewGuid(), 1, 0);
            }

            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(HangingStream());

            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            window.DataContext = vm;
            window.AttachPresenter(presenter);
            vm.AttachPresenter(presenter);
            window.Show();

            try
            {
                window.FindName("TranslateButton").ShouldBeNull();
                var stopBtn = (Wpf.Ui.Controls.Button)window.FindName("StopButton");
                stopBtn.ShouldNotBeNull();

                vm.InputText = "Test";
                vm.ExecuteTranslateFromUser();

                vm.IsTranslating.ShouldBeTrue();
                stopBtn.Visibility.ShouldBe(Visibility.Visible);
                vm.TranslateCommand.CanExecute(null).ShouldBeTrue();
                vm.StopCommand.CanExecute(null).ShouldBeTrue();

                vm.OnWindowHidden();
                streamGate.TrySetResult();
                await vm.WaitForActiveWorkAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();

                vm.IsTranslating.ShouldBeFalse();
                stopBtn.Visibility.ShouldBe(Visibility.Collapsed);
                vm.TranslateCommand.CanExecute(null).ShouldBeTrue();
                vm.StopCommand.CanExecute(null).ShouldBeFalse();
            }
            finally
            {
                streamGate.TrySetResult();
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task TopBar_ShowsDetectedSourceLanguageOnlyWhenAutoAndResolved()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();
            var streamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<TranslationEvent> Stream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                await streamGate.Task.WaitAsync(ct);
                yield return new TranslationFinished(Guid.NewGuid(), 1, 0);
            }
            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci => Stream(ci.Arg<CancellationToken>()));

            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            window.DataContext = vm;
            window.AttachPresenter(presenter);
            vm.AttachPresenter(presenter);
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Width = 280;
            window.Show();
            window.UpdateLayout();

            try
            {
                var detectedTb = (TextBlock)window.FindName("DetectedSourceLanguageTextBlock");
                detectedTb.ShouldNotBeNull();
                detectedTb.Visibility.ShouldBe(Visibility.Collapsed);
                var swapBtn = (System.Windows.Controls.Button)window.FindName("SwapLanguagesButton");
                swapBtn.ShouldNotBeNull();
                        swapBtn.IsEnabled.ShouldBeFalse();

                vm.SourceLanguage = "auto";
                swapBtn.IsEnabled.ShouldBeFalse();
                var detectedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                System.ComponentModel.PropertyChangedEventHandler onChanged = (_, _) =>
                {
                    if (vm.HasDetectedSourceLanguage && vm.CanSwapLanguages) detectedReady.TrySetResult();
                };
                vm.PropertyChanged += onChanged;
                try
                {
                    vm.InputText = "Hello";
                    vm.ExecuteTranslateFromUser();
                    if (!(vm.HasDetectedSourceLanguage && vm.CanSwapLanguages))
                        await detectedReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { vm.PropertyChanged -= onChanged; }
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                swapBtn.IsEnabled.ShouldBeTrue();
                detectedTb.Visibility.ShouldBe(Visibility.Visible);
                detectedTb.Text.ShouldNotBeNullOrWhiteSpace();
                swapBtn.IsEnabled.ShouldBeTrue();

                var rect = detectedTb.TransformToAncestor(window).TransformBounds(new Rect(detectedTb.RenderSize));
                rect.Left.ShouldBeGreaterThanOrEqualTo(0);
                rect.Right.ShouldBeLessThanOrEqualTo(window.ActualWidth + 0.5);

                vm.SourceLanguage = "en";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                detectedTb.Visibility.ShouldBe(Visibility.Collapsed);
            }
            finally
            {
                streamGate.TrySetResult();
                await vm.WaitForActiveWorkAsync();
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PinButton_TogglesPinnedStateAndUpdatesAccessibleTooltip()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            window.DataContext = vm;
            window.AttachPresenter(presenter);
            vm.AttachPresenter(presenter);
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            window.UpdateLayout();

            try
            {
                var pinBtn = (Wpf.Ui.Controls.Button)window.FindName("PinButton");
                pinBtn.ShouldNotBeNull();

                vm.IsPinned.ShouldBeFalse();
                pinBtn.ToolTip.ShouldBe(UiText.TranslatePinWindow);
                System.Windows.Automation.AutomationProperties.GetName(pinBtn).ShouldBe(UiText.TranslatePinWindow);
                pinBtn.Icon.ShouldBeOfType<Wpf.Ui.Controls.SymbolIcon>().Filled.ShouldBeFalse();

                pinBtn.Command.Execute(pinBtn.CommandParameter);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();

                vm.IsPinned.ShouldBeTrue();
                pinBtn.ToolTip.ShouldBe(UiText.TranslateUnpinWindow);
                System.Windows.Automation.AutomationProperties.GetName(pinBtn).ShouldBe(UiText.TranslateUnpinWindow);
                pinBtn.Icon.ShouldBeOfType<Wpf.Ui.Controls.SymbolIcon>().Filled.ShouldBeTrue();
            }
            finally
            {
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task StaleResults_FadeResultCardsAndShowLastResultTag()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();
            var callCount = 0;
            var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<TranslationEvent> CompletedStream()
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                yield return new ProviderStarted(Guid.NewGuid(), "p1");
                yield return new ProviderDelta(Guid.NewGuid(), "p1", "你好");
                yield return new ProviderCompleted(Guid.NewGuid(), "p1", "你好", TimeSpan.FromMilliseconds(10), false, false, null);
                yield return new TranslationFinished(Guid.NewGuid(), 1, 0);
            }
            async IAsyncEnumerable<TranslationEvent> SecondStream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                yield return new ProviderStarted(Guid.NewGuid(), "p1");
                await secondGate.Task.WaitAsync(ct);
                yield return new TranslationFinished(Guid.NewGuid(), 1, 0);
            }
            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci => Interlocked.Increment(ref callCount) == 1 ? CompletedStream() : SecondStream(ci.Arg<CancellationToken>()));

            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            window.DataContext = vm;
            window.AttachPresenter(presenter);
            vm.AttachPresenter(presenter);
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            window.UpdateLayout();

            try
            {
                vm.InputText = "Hello";
                vm.ExecuteTranslateFromUser();
                await vm.WaitForActiveWorkAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();

                var scrollViewer = (ScrollViewer)window.FindName("CardsScrollViewer");
                var inputTb = (TextBox)window.FindName("InputTextBox");
                inputTb.Text = "Hello world";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();

                vm.HasStaleResults.ShouldBeTrue();
                var translatedTextBoxes = FindVisualChildren<TextBox>(scrollViewer)
                    .Where(tb => tb.Opacity < 0.99 && tb.Opacity > 0.1)
                    .ToList();
                for (var i = 0; i < 20 && translatedTextBoxes.Count == 0; i++)
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    window.UpdateLayout();
                    translatedTextBoxes = FindVisualChildren<TextBox>(scrollViewer).Where(tb => tb.Opacity < 0.99 && tb.Opacity > 0.1).ToList();
                }
                translatedTextBoxes.ShouldNotBeEmpty();

                var staleTags = FindVisualChildren<TextBlock>(scrollViewer)
                    .Where(tb => tb.Text == UiText.TranslateLastResult && tb.IsVisible)
                    .ToList();
                staleTags.ShouldNotBeEmpty();
                foreach (var tagTb in staleTags)
                {
                    DependencyObject? current = tagTb;
                    while (current is not null && !ReferenceEquals(current, scrollViewer))
                    {
                        if (current is UIElement uiElement)
                            uiElement.Opacity.ShouldBe(1.0);
                        current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                    }
                }

                vm.ExecuteTranslateFromUser();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();

                vm.HasStaleResults.ShouldBeFalse();
                FindVisualChildren<TextBox>(scrollViewer).ShouldAllBe(tb => tb.Opacity == 1.0);
                FindVisualChildren<TextBlock>(scrollViewer)
                    .Where(tb => tb.Text == UiText.TranslateLastResult && tb.IsVisible)
                    .ShouldBeEmpty();
                FindVisualChildren<Border>(scrollViewer)
                    .Where(b => b.Opacity < 0.99 && b.Opacity > 0.1)
                    .ShouldBeEmpty();
                FindVisualChildren<TextBlock>(scrollViewer)
                    .Where(tb => tb.Text == UiText.TranslateLastResult && tb.IsVisible)
                    .ShouldBeEmpty();
            }
            finally
            {
                secondGate.TrySetResult();
                await vm.WaitForActiveWorkAsync();
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task StopButton_VisibleOnlyWhileTranslating_ClickShowsStoppedStatusAndKeepsPartialCards()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();
            var streamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deltaYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<TranslationEvent> PartialStream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                yield return new ProviderStarted(Guid.NewGuid(), "p1");
                yield return new ProviderDelta(Guid.NewGuid(), "p1", "Partial");
                deltaYielded.TrySetResult();
                await streamGate.Task.WaitAsync(ct);
            }
            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci => PartialStream(ci.Arg<CancellationToken>()));

            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            window.DataContext = vm;
            window.AttachPresenter(presenter);
            vm.AttachPresenter(presenter);
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            window.UpdateLayout();

            try
            {
                var stopBtn = (Wpf.Ui.Controls.Button)window.FindName("StopButton");
                var statusTb = (TextBlock)window.FindName("StatusMessageTextBlock");
                stopBtn.Visibility.ShouldBe(Visibility.Collapsed);

            vm.InputText = "Test";
            vm.ExecuteTranslateFromUser();
            await deltaYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.UpdateLayout();
                stopBtn.Visibility.ShouldBe(Visibility.Visible);
                vm.Cards.Count.ShouldBe(1);
                var scrollViewer = (ScrollViewer)window.FindName("CardsScrollViewer");
                var cardBorder = FindVisualChildren<Border>(scrollViewer)
                    .First(b => b.DataContext is ProviderResultCardViewModel);
                var heightWhileStreaming = cardBorder.ActualHeight;
                heightWhileStreaming.ShouldBeGreaterThan(0);

                var invokeProvider = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(stopBtn)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
                invokeProvider.Invoke();
                await vm.WaitForActiveWorkAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();

                stopBtn.Visibility.ShouldBe(Visibility.Collapsed);
                statusTb.Visibility.ShouldBe(Visibility.Visible);
                statusTb.Text.ShouldBe(UiText.TranslateStopped);
                vm.Cards.Count.ShouldBe(1);
                cardBorder.ActualHeight.ShouldBe(heightWhileStreaming);
            }
            finally
            {
                streamGate.TrySetResult();
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task EnterToTranslateFalse_RequiresCtrlEnterAndCancelsPendingDebounce()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var initialSettings = new AppSettings
            {
                TranslateWindow = new TranslateWindowSettings { EnterToTranslate = false }
            };
            using var provider = CreateServiceProvider(initialSettings, fakeTime);
            var orchestrator = provider.GetRequiredService<ITranslationOrchestrator>();
            var streamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<TranslationEvent> PendingStream()
            {
                var p1 = new ProviderDescriptor("p1", "P1", "Kind", null, true);
                yield return new TranslationStarted(Guid.NewGuid(), new ResolvedLanguagePair("en", "zh-Hans", false), [p1]);
                await streamGate.Task;
                yield return new TranslationFinished(Guid.NewGuid(), 1, 0);
            }
            orchestrator.TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => PendingStream());

            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();
            using var hwndSource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "TestHwnd", IntPtr.Zero);
            var previousModifiers = TranslateWindow.TestKeyboardModifiersProvider;
            try
            {
                TranslateWindow.TestKeyboardModifiersProvider = () => ModifierKeys.None;
                window.DataContext = vm;
                window.AttachPresenter(presenter);
                vm.AttachPresenter(presenter);
                window.Left = -20000;
                window.Top = -20000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                vm.InputText = "Sample Text";
                fakeTime.Advance(TimeSpan.FromMilliseconds(500));
                window.UpdateLayout();
                var inputTb = (TextBox)window.FindName("InputTextBox");

                var enterArgs = new KeyEventArgs(Keyboard.PrimaryDevice, hwndSource, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                inputTb.RaiseEvent(enterArgs);
                enterArgs.Handled.ShouldBeFalse();
                vm.IsTranslating.ShouldBeFalse();

                TranslateWindow.TestKeyboardModifiersProvider = () => ModifierKeys.Control;
                var ctrlEnterArgs = new KeyEventArgs(Keyboard.PrimaryDevice, hwndSource, 0, Key.Enter)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                inputTb.RaiseEvent(ctrlEnterArgs);
                ctrlEnterArgs.Handled.ShouldBeTrue();
                vm.IsTranslating.ShouldBeTrue();

                streamGate.TrySetResult();
                await vm.WaitForActiveWorkAsync();
                vm.IsTranslating.ShouldBeFalse();

                fakeTime.Advance(TimeSpan.FromMilliseconds(2000));
                await vm.WaitForActiveWorkAsync();
                orchestrator.Received(1).TranslateAsync(Arg.Any<TranslationRequest>(), Arg.Any<CancellationToken>());
            }
            finally
            {
                streamGate.TrySetResult();
                TranslateWindow.TestKeyboardModifiersProvider = previousModifiers;
                window.ForceClose = true;
                window.DataContext = null;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task EscapeKey_HidesWindowWhenUnpinned()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            using var hwndSource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "TestHwnd", IntPtr.Zero);
            using var op = presenter.TryBeginOperation();
            var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "Sample", AutoTranslate = false });
            shown.ShouldBeTrue();
            var window = (TranslateWindow)presenter.Window!;
            var hiddenTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnIsVisibleChanged(object? s, DependencyPropertyChangedEventArgs e)
            {
                if (!window.IsVisible)
                    hiddenTcs.TrySetResult();
            }
            window.IsVisibleChanged += OnIsVisibleChanged;
            try
            {
                presenter.IsVisible.ShouldBeTrue();
                presenter.ViewModel!.IsPinned.ShouldBeFalse();

                var escArgs = new KeyEventArgs(Keyboard.PrimaryDevice, hwndSource, 0, Key.Escape)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(escArgs);
                escArgs.Handled.ShouldBeTrue();
                await hiddenTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.IsVisible.ShouldBeFalse();
                presenter.IsVisible.ShouldBeFalse();
            }
            finally
            {
                window.IsVisibleChanged -= OnIsVisibleChanged;
                op?.Dispose();
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task InputTextBox_LostKeyboardFocus_ClearsImeCompositionSuppression()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var initialSettings = new AppSettings
            {
                Translation = new TranslationSettings { MaxInputChars = 5000 }
            };
            using var provider = CreateServiceProvider(initialSettings, fakeTime);
            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            using var op = presenter.TryBeginOperation();
            var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "hello", AutoTranslate = false });
            shown.ShouldBeTrue();
            var window = (TranslateWindow)presenter.Window!;
            var vm = (TranslateWindowViewModel)window.DataContext!;
            try
            {
                window.UpdateLayout();
                var inputBox = (System.Windows.Controls.TextBox)window.FindName("InputTextBox")!;
                vm.SetImeComposing(true);

                var lostFocusArgs = new KeyboardFocusChangedEventArgs(
                    Keyboard.PrimaryDevice,
                    0,
                    inputBox,
                    window)
                {
                    RoutedEvent = UIElement.LostKeyboardFocusEvent
                };
                inputBox.RaiseEvent(lostFocusArgs);

                vm.InputText = "hello world";
                fakeTime.Advance(TimeSpan.FromMilliseconds(1000));
                vm.IsTranslating.ShouldBeTrue();
            }
            finally
            {
                op?.Dispose();
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task Narrow280pxWindow_EnUs_TopBarControlsFitWithoutClipping()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var previousCulture = System.Globalization.CultureInfo.CurrentUICulture;
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            using var op = presenter.TryBeginOperation();
            var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "hello", AutoTranslate = false });
            shown.ShouldBeTrue();
            var window = (TranslateWindow)presenter.Window!;
            window.Width = 280;
            window.UpdateLayout();
            try
            {
                var names = new[]
                {
                    "SourceLangComboBox",
                    "DetectedSourceLanguageTextBlock",
                    "PinButton",
                    "SettingsButton",
                    "TargetLangComboBox"
                };
                foreach (var name in names)
                {
                    var element = (FrameworkElement)window.FindName(name)!;
                    var topLeft = element.TransformToAncestor(window).Transform(new Point(0, 0));
                    var right = topLeft.X + element.ActualWidth;
                    topLeft.X.ShouldBeGreaterThanOrEqualTo(-0.5, $"{name} left={topLeft.X}");
                    right.ShouldBeLessThanOrEqualTo(window.ActualWidth + 0.5, $"{name} right={right} > width={window.ActualWidth}");
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = previousCulture;
                op?.Dispose();
                await presenter.ShutdownAsync();
            }
        });
    }
    [Fact]
    public async Task ResultCard_ErrorIconCollapsedOnSuccess_VisibleOnFailure()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var presenter = scope.ServiceProvider.GetRequiredService<TranslateWindowPresenter>();
            using var op = presenter.TryBeginOperation();
            var shown = await presenter.TryShowAsync(op!, new TranslateWindowRequest { Text = "hello", AutoTranslate = false });
            shown.ShouldBeTrue();
            var window = (TranslateWindow)presenter.Window!;
            var vm = (TranslateWindowViewModel)window.DataContext;
            try
            {
                var successCard = new ProviderResultCardViewModel(
                    operationId: Guid.NewGuid(),
                    providerId: "p-ok",
                    displayName: "Provider OK",
                    model: "m1",
                    requestId: Guid.NewGuid(),
                    onRetry: _ => { },
                    onCopy: _ => { },
                    onGoToSettings: _ => { },
                    canRetryPredicate: _ => true);
                successCard.SetCompleted("translated", TimeSpan.FromMilliseconds(10), fromCache: false, isModelTruncated: false, isLocallyTruncated: false);

                vm.Cards.Clear();
                vm.Cards.Add(successCard);
                window.UpdateLayout();

                var cardsScrollViewer = (DependencyObject)window.FindName("CardsScrollViewer")!;
                FindVisualChildren<Wpf.Ui.Controls.SymbolIcon>(cardsScrollViewer)
                    .Where(icon => icon.Symbol == Wpf.Ui.Controls.SymbolRegular.ErrorCircle16 && icon.IsVisible)
                    .ShouldBeEmpty();

                var failedCard = new ProviderResultCardViewModel(
                    operationId: Guid.NewGuid(),
                    providerId: "p-fail",
                    displayName: "Provider Fail",
                    model: "m1",
                    requestId: Guid.NewGuid(),
                    onRetry: _ => { },
                    onCopy: _ => { },
                    onGoToSettings: _ => { },
                    canRetryPredicate: _ => true);
                failedCard.SetFailed("Request failed", showSettings: false, TimeSpan.FromMilliseconds(20));

                vm.Cards.Clear();
                vm.Cards.Add(failedCard);
                window.UpdateLayout();

                FindVisualChildren<Wpf.Ui.Controls.SymbolIcon>(cardsScrollViewer)
                    .Count(icon => icon.Symbol == Wpf.Ui.Controls.SymbolRegular.ErrorCircle16 && icon.IsVisible)
                    .ShouldBe(1);
            }
            finally
            {
                op?.Dispose();
                await presenter.ShutdownAsync();
            }
        });
    }


    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
