using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisThemeDictionaryTests
{
    // 直接调内部静态方法：不经过 dispatcher，结果确定，这是主断言。
    // 每一行都先换成相反的主题、再换成目标主题：宿主本来就合着浅色字典，只换一次的话 Light 那一行恒真，
    // 而「把深色换回浅色」这个方向根本没人断言。
    [Theory]
    [InlineData(ApplicationTheme.Light, ApplicationTheme.Dark, "Themes/Iris.Light.xaml", "Themes/Iris.Dark.xaml")]
    [InlineData(ApplicationTheme.Dark, ApplicationTheme.Light, "Themes/Iris.Dark.xaml", "Themes/Iris.Light.xaml")]
    public Task ApplyIrisThemeDictionary_ReplacesTheMergedIrisDictionary(
        ApplicationTheme theme, ApplicationTheme opposite, string expectedFile, string absentFile) =>
        WpfTestRunner.RunAsync(() =>
        {
            try
            {
                // 基线必须在第一次替换之前取：「先删再追加到末尾」的实现在第一次调用时就把字典挪走了，
                // 之后再怎么换下标都不变——在那之后才取基线，位置断言就守不住任何东西。
                var indexAtStart = IrisThemeDictionaryIndexes().ShouldHaveSingleItem();

                ThemeService.ApplyIrisThemeDictionary(opposite);
                IrisThemeDictionaryIndexes().ShouldHaveSingleItem().ShouldBe(indexAtStart);
                MergedSources()[indexAtStart].EndsWith(absentFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();

                ThemeService.ApplyIrisThemeDictionary(theme);

                // 就地替换：仍然恰好一本 Iris 主题字典，而且还在原来的位置（位置决定覆盖优先级）。
                IrisThemeDictionaryIndexes().ShouldHaveSingleItem().ShouldBe(indexAtStart);
                MergedSources()[indexAtStart].EndsWith(expectedFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            }
            finally
            {
                RestoreLightDictionary();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task Apply_ThroughThemeService_SwapsTheIrisDictionary() => WpfTestRunner.RunAsync(() =>
    {
        // ThemeService.Apply(AppTheme) 忽略传入值、改用设置里的主题（Apply → ApplyEffectiveTheme），
        // 所以把主题写进设置，走的才是与应用一致的那条路。传 Light 却能出现 Dark 字典，才算证明了这一点。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Dark } });
        using var service = new ThemeService(settings, visualThemeApplier: _ => { });
        try
        {
            // 起点必须是浅色：别的测试可能把深色字典留在共享 Application 里，那样下面的断言不换字典也会绿。
            RestoreLightDictionary();
            MergedSources().ShouldNotContain(source => source.EndsWith("Themes/Iris.Dark.xaml", StringComparison.OrdinalIgnoreCase));

            service.Apply(AppTheme.Light);

            MergedSources().ShouldContain(source => source.EndsWith("Themes/Iris.Dark.xaml", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RestoreLightDictionary();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_DoesNotLetAMaterialFailureEscape() => WpfTestRunner.RunAsync(() =>
    {
        // 必须是显式 Light：System 主题那条路外层本来就有 catch（SafeApplySystemTheme），测不出东西。
        // 显式主题这条路在启动时由 AppBootstrapper 同步调用、外层没有 catch——异常逃出去就是应用起不来。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        var probed = false;
        var failing = new WindowMaterialService(
            () =>
            {
                probed = true;
                throw new InvalidOperationException("probe");
            },
            () => true);
        using var service = new ThemeService(settings, visualThemeApplier: _ => { }, windowMaterialService: failing);

        Should.NotThrow(() => service.Apply(AppTheme.Light));
        // 探针真的被调到过：否则「没抛出来」只说明根本没走到材质服务。
        probed.ShouldBeTrue();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_ReappliesTheWindowMaterialAfterTheThemeChange() => WpfTestRunner.RunAsync(() =>
    {
        // 接缝里做的就是生产分支做的那一件事（Apply(theme, None)），所以这条走的是与应用一致的顺序：
        // 先切主题（把窗口背景重置成不透明）→ 再由 ThemeService 调材质服务把它清回透明。
        // 背板本身（DWM 读回）由 WindowMaterialServiceTests 守着，这里只守「ThemeService 真的调了它」。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        var material = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current!.MainWindow;
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        byte? alphaRightAfterTheThemeChange = null;
        using var service = new ThemeService(
            settings,
            visualThemeApplier: theme =>
            {
                ApplicationThemeManager.Apply(theme, WindowBackdropType.None);
                alphaRightAfterTheThemeChange = (window.Background as SolidColorBrush)?.Color.A;
            },
            windowMaterialService: material);
        try
        {
            window.Show();
            // Apply(theme, None) 只作用于 Application.MainWindow，所以显式指定成本窗口。
            Application.Current.MainWindow = window;
            material.ApplyAcrylic(window);

            service.Apply(AppTheme.Light);

            // 前置：切主题确实把背景重置成了不透明——否则下面的终态断言什么也证明不了。
            alphaRightAfterTheThemeChange.ShouldBe((byte?)255, "null means the theme seam never ran");
            // 终态：ThemeService 在切完主题之后调了 RefreshOpenWindows。
            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
            RestoreLightDictionary();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_KeepsASolidWindowOpaque_OnTheProductionPath() => WpfTestRunner.RunAsync(() =>
    {
        // 设计 §3.3 / §8.2 点名要的那一条：切主题后，纯色窗口的背景仍是不透明的 SolidColorBrush。
        // 不传接缝，走的是生产分支里那句 ApplicationThemeManager.Apply(theme, WindowBackdropType.None)；
        // 它只作用于 Application.MainWindow，所以把纯色窗口显式设成 MainWindow。
        // 材质服务声称支持亚克力：普通 Window 不是 FluentWindow，RefreshOpenWindows 不该碰它。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        var material = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current!.MainWindow;
        var window = new Window { Content = "placeholder", Background = Brushes.White };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        using var service = new ThemeService(settings, windowMaterialService: material);
        try
        {
            window.Show();
            Application.Current.MainWindow = window;

            service.Apply(AppTheme.Light);

            // 生产分支确实动过这个窗口：切主题会把背景换成主题自己的画刷，不再是初始那一支。
            // 没有这一句，初始的不透明白色就足以让下面的断言恒真。
            window.Background.ShouldNotBeSameAs(Brushes.White);
            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)255);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
            RestoreLightDictionary();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void ThemeService_ResolvesTheMaterialServiceFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISettingsService>());
        services.AddSingleton<WindowMaterialService>();
        services.AddSingleton<ThemeService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<ThemeService>().HasWindowMaterialService.ShouldBeTrue();
    }

    private static string[] MergedSources() =>
        Application.Current!.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source?.OriginalString ?? string.Empty)
            .ToArray();

    private static int[] IrisThemeDictionaryIndexes() =>
        MergedSources()
            .Select((source, index) => (source, index))
            .Where(entry => entry.source.EndsWith("Themes/Iris.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || entry.source.EndsWith("Themes/Iris.Dark.xaml", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .ToArray();

    // 整个测试进程只有一个 Application、没有任何「跑前重置字典」的逻辑：不还原，深色字典就留给后面的测试了。
    private static void RestoreLightDictionary() => ThemeService.ApplyIrisThemeDictionary(ApplicationTheme.Light);
}
