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

public sealed class TranslateWindowUiTests
{
    private static ServiceProvider CreateServiceProvider(AppSettings? initialSettings = null)
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
        services.AddSingleton<IUiDispatcher, ImmediateUiDispatcher>();
        services.AddSingleton<ThemeService>();

        services.AddSingleton<TranslateWindowPresenter>();
        services.AddSingleton<ITranslateWindowPresenter>(sp => sp.GetRequiredService<TranslateWindowPresenter>());
        services.AddSingleton<ITranslateWindowShutdownCoordinator>(sp => sp.GetRequiredService<TranslateWindowPresenter>());

        services.AddScoped<TranslateWindowViewModel>(sp => new TranslateWindowViewModel(
            settingsService,
            orchestrator,
            clipboard,
            settingsPresenter,
            sp.GetRequiredService<IUiDispatcher>(),
            NullLogger<TranslateWindowViewModel>.Instance));

        services.AddTransient<TranslateWindow>();

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(460, Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData(460, Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    [InlineData(280, Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData(280, Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    public async Task Layout_AtDifferentWidthsAndThemes_ControlsAreAccessibleAndNotClipped(double width, Wpf.Ui.Appearance.ApplicationTheme theme)
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
                var inputTb = (TextBox)window.FindName("InputTextBox");
                var sourceCombo = (ComboBox)window.FindName("SourceLangComboBox");
                var targetCombo = (ComboBox)window.FindName("TargetLangComboBox");
                var translateBtn = (Wpf.Ui.Controls.Button)window.FindName("TranslateButton");
                var scrollViewer = (ScrollViewer)window.FindName("CardsScrollViewer");

                inputTb.ShouldNotBeNull();
                sourceCombo.ShouldNotBeNull();
                targetCombo.ShouldNotBeNull();
                translateBtn.ShouldNotBeNull();
                scrollViewer.ShouldNotBeNull();

                inputTb.ActualWidth.ShouldBeGreaterThan(100);
                sourceCombo.ActualWidth.ShouldBeGreaterThan(40);
                targetCombo.ActualWidth.ShouldBeGreaterThan(40);
                translateBtn.ActualWidth.ShouldBeGreaterThan(30);

                var swapBtn = (Wpf.Ui.Controls.Button)window.FindName("SwapLanguagesButton");
                foreach (var element in new FrameworkElement[] { sourceCombo, swapBtn, targetCombo, translateBtn })
                {
                    var rect = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                    rect.Left.ShouldBeGreaterThanOrEqualTo(0, $"{element.Name} 左边缘越界");
                    rect.Right.ShouldBeLessThanOrEqualTo(window.ActualWidth + 0.5, $"{element.Name} 右边缘超出窗口可视宽度");
                }
            }
            finally
            {
                window.DataContext = null;
                window.Close();
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

            try
            {
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
                    TranslateWindow.TestKeyboardModifiersProvider = null;
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
                window.DataContext = null;
                window.Close();
            }
        });
    }
}
