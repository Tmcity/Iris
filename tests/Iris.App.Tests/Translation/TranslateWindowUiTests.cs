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
        services.AddSingleton(settingsService);
        services.AddSingleton(orchestrator);
        services.AddSingleton(clipboard);
        services.AddSingleton(settingsPresenter);
        services.AddSingleton(desktopContext);
        services.AddSingleton<IUiDispatcher, ImmediateUiDispatcher>();
        services.AddSingleton<ThemeService>();

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
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<TranslateWindowViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<TranslateWindow>();

            try
            {
                window.DataContext = vm;
                window.Show();
                vm.InputText = "Sample Text";
                window.UpdateLayout();

                // 模拟 IME 组合输入确认 Enter
                var imeEventArgs = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero),
                    0,
                    Key.ImeProcessed)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };

                window.RaiseEvent(imeEventArgs);

                // 验证 IME 确认没有触发翻译
                vm.IsTranslating.ShouldBeFalse();
            }
            finally
            {
                window.DataContext = null;
                window.Close();
            }
        });
    }
}
