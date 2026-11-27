using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Settings;

// T-041 阶段 2 · Task 4：设置窗口骨架（背板着色层、导航、底栏）。
// 导航宽度的两个数（176 / 48）与阈值 680 来自设计文档；这里按字面钉住，不从主题资源里读。
[Collection("IrisThemeResources")]
public sealed class SettingsWindowNavigationLayoutTests
{
    private const double WideNavWidth = 212;
    private const double CompactNavWidth = 48;

    private sealed record NavItem(string Name, string PageKey, Func<string> Text, Wpf.Ui.Controls.SymbolRegular Icon);

    // 顺序就是界面上的显示顺序。Text 用委托而不是字符串：UiText 的值随界面语言变，不能在类型初始化时取死。
    private static readonly NavItem[] NavItems =
    [
        new("NavGeneral", "general", () => UiText.SettingsNavGeneral, Wpf.Ui.Controls.SymbolRegular.Settings20),
        new("NavTranslation", "translation", () => UiText.SettingsNavTranslation, Wpf.Ui.Controls.SymbolRegular.Translate20),
        new("NavHotkeys", "hotkeys", () => UiText.SettingsNavHotkeys, Wpf.Ui.Controls.SymbolRegular.Options20),
        new("NavSelection", "selection", () => UiText.SettingsNavSelection, Wpf.Ui.Controls.SymbolRegular.CursorClick20),
        new("NavOcr", "ocr", () => UiText.OcrPageTitle, Wpf.Ui.Controls.SymbolRegular.ScanText20),
        new("NavScreenshot", "screenshot", () => UiText.ScreenshotPageTitle, Wpf.Ui.Controls.SymbolRegular.Screenshot20),
        new("NavNetwork", "network", () => UiText.NetworkTitle, Wpf.Ui.Controls.SymbolRegular.Globe20),
        new("NavHistory", "history", () => UiText.HistoryTitle, Wpf.Ui.Controls.SymbolRegular.History20),
    ];

    // 照抄 SettingsWindowTranslationUiTests.CreateServiceProvider（那边是 private），去掉了用不到的部分。
    private static ServiceProvider CreateServiceProvider()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var autoStartService = Substitute.For<IAutoStartService>();
        var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
        var receiptManager = new SettingsApplyReceiptManager();
        var uiDispatcher = new ImmediateUiDispatcher();

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(autoStartService);
        services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);
        services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<ThemeService>();

        services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            receiptManager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance));

        services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
            settingsService,
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
            editCoordinator,
            receiptManager,
            uiDispatcher,
            NullLogger<SettingsViewModel>.Instance));

        return services.BuildServiceProvider();
    }

    // 建窗口、挂视图模型，按需 Show()；无论断言成败都关窗。必须在 WpfTestRunner 的 STA 线程里调用。
    private static void WithWindow(double width, double height, bool show, Action<SettingsWindow, SettingsViewModel> body)
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow();
        try
        {
            window.DataContext = vm;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Width = width;
            window.Height = height;
            if (show)
            {
                window.Show();
                window.UpdateLayout();
            }

            body(window, vm);
        }
        finally
        {
            window.DataContext = null;
            window.ForceClose = true;
            window.Close();
        }
    }

    private static T Named<T>(SettingsWindow window, string name) =>
        window.FindName(name).ShouldBeOfType<T>($"element named {name}");

    private static Border Nav(SettingsWindow window, NavItem item) => Named<Border>(window, item.Name);

    private static (Border Accent, Wpf.Ui.Controls.SymbolIcon Icon, TextBlock Label) Parts(Border nav)
    {
        var grid = nav.Child.ShouldBeOfType<Grid>();
        return (
            grid.Children.OfType<Border>().ShouldHaveSingleItem(),
            grid.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().ShouldHaveSingleItem(),
            grid.Children.OfType<TextBlock>().ShouldHaveSingleItem());
    }

    // 导航外框与底栏外框都没有名字：从一个有名字的后代沿逻辑树往上找。
    // NavOcr 与 StatusMessageTextBlock 改造前就有名字，所以改造前这里取得到，红会红在后面的断言上。
    private static Border NavHost(SettingsWindow window)
    {
        var panel = Named<Border>(window, "NavOcr").Parent.ShouldBeOfType<StackPanel>();
        var scroll = panel.Parent.ShouldBeOfType<ScrollViewer>();
        return scroll.Parent.ShouldBeOfType<Border>();
    }

    private static Border Footer(SettingsWindow window)
    {
        var statusGrid = Named<TextBlock>(window, "StatusMessageTextBlock").Parent.ShouldBeOfType<Grid>();
        var footerGrid = statusGrid.Parent.ShouldBeOfType<Grid>();
        return footerGrid.Parent.ShouldBeOfType<Border>();
    }

    private static Color ColorOf(object? brush) => brush.ShouldBeOfType<SolidColorBrush>().Color;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Fact]
    public Task Constructor_LeavesTheNavigationWideAtTheDefaultWidth() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, _) =>
        {
            var column = Named<ColumnDefinition>(window, "NavColumnDef");
            column.Width.IsAbsolute.ShouldBeTrue();
            column.Width.Value.ShouldBe(WideNavWidth);
            Named<TextBlock>(window, "NavTitleText").Visibility.ShouldBe(Visibility.Visible);
        });
    });

    [Theory]
    [InlineData(600d, CompactNavWidth)]
    [InlineData(679d, CompactNavWidth)]
    [InlineData(679.99, CompactNavWidth)]
    [InlineData(680d, WideNavWidth)]
    [InlineData(760d, WideNavWidth)]
    public Task ApplyAdaptiveLayout_SwitchesTheNavigationAtTheCompactThreshold(double width, double expectedNavWidth) => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, _) =>
        {
            var column = Named<ColumnDefinition>(window, "NavColumnDef");
            var title = Named<TextBlock>(window, "NavTitleText");

            // 先切到相反的一档再切回来：证明每次调用都会写导航，不被旧分栏逻辑的「模式没变就返回」挡住。
            window.ApplyAdaptiveLayout(expectedNavWidth == WideNavWidth ? 600 : 760);
            window.ApplyAdaptiveLayout(width);

            column.Width.IsAbsolute.ShouldBeTrue();
            column.Width.Value.ShouldBe(expectedNavWidth);
            title.Visibility.ShouldBe(expectedNavWidth == WideNavWidth ? Visibility.Visible : Visibility.Collapsed);
        });
    });

    [Theory]
    [InlineData(600d, 440d, CompactNavWidth)]
    [InlineData(760d, 520d, WideNavWidth)]
    public Task ShownWindow_LaysOutTheNavigationColumnAtTheExpectedWidth(double width, double height, double expectedNavWidth) => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(width, height, show: true, (window, _) =>
        {
            Named<ColumnDefinition>(window, "NavColumnDef").ActualWidth.ShouldBe(expectedNavWidth, 1d);
            NavHost(window).ActualWidth.ShouldBe(expectedNavWidth, 1d);

            // 默认页是「常规」：它的滚动区左缘必须正好接在导航右缘上（相对根 Grid 量，不受窗口外框影响）。
            var root = window.Content.ShouldBeOfType<Grid>();
            var page = Named<ScrollViewer>(window, "GeneralPageScroll");
            page.IsVisible.ShouldBeTrue();
            page.TransformToAncestor(root).Transform(new Point(0, 0)).X.ShouldBe(expectedNavWidth, 1d);
        });
    });

    [Fact]
    public Task NavItems_AreNamedBordersWithIconLabelToolTipAndAutomationName() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, _) =>
        {
            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                string text = item.Text();
                text.ShouldNotBeNullOrWhiteSpace(item.Name);

                (nav.ToolTip as string).ShouldBe(text, item.Name);
                System.Windows.Automation.AutomationProperties.GetName(nav).ShouldBe(text, item.Name);

                // 这两条来自主题层的 IrisNavItem：证明内联样式是 BasedOn 它，且没有本地值把它盖掉。
                nav.Padding.ShouldBe(new Thickness(8), item.Name);
                nav.Cursor.ShouldBe(Cursors.Hand, item.Name);

                var (accent, icon, label) = Parts(nav);
                icon.Symbol.ShouldBe(item.Icon, item.Name);
                icon.FontSize.ShouldBe(20d, item.Name);
                label.Text.ShouldBe(text, item.Name);
                label.TextWrapping.ShouldBe(TextWrapping.NoWrap, item.Name);
                label.TextTrimming.ShouldBe(TextTrimming.CharacterEllipsis, item.Name);
                label.Margin.ShouldBe(new Thickness(12, 0, 0, 0), item.Name);
                accent.Width.ShouldBe(3d, item.Name);
                accent.Height.ShouldBe(16d, item.Name);
                accent.CornerRadius.ShouldBe(new CornerRadius(2), item.Name);
                ColorOf(accent.Background).ShouldBe(ColorOf(window.FindResource("AccentFillColorDefaultBrush")), item.Name);
            }
        });
    });

    [Fact]
    public Task NavItems_OnlyTheCurrentPageShowsTheAccentBarAndTheSubtleFill() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, vm) =>
        {
            Color subtle = ColorOf(window.FindResource("SubtleFillColorSecondaryBrush"));
            subtle.ShouldNotBe(Colors.Transparent);

            foreach (var current in NavItems)
            {
                vm.CurrentPageKey = current.PageKey;
                window.UpdateLayout();

                foreach (var item in NavItems)
                {
                    bool selected = item.Name == current.Name;
                    string because = $"{item.Name} while the page is {current.PageKey}";
                    var nav = Nav(window, item);
                    Parts(nav).Accent.Visibility.ShouldBe(selected ? Visibility.Visible : Visibility.Collapsed, because);
                    ColorOf(nav.Background).ShouldBe(selected ? subtle : Colors.Transparent, because);
                }
            }
        });
    });

    [Fact]
    public Task NavItems_StillRouteMouseUpThroughTheExistingHandlers() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, vm) =>
        {
            vm.CurrentPageKey.ShouldBe("general");

            // 从第二项开始点、最后点第一项：每一次点击都必须真的换页。
            foreach (var item in NavItems.Skip(1).Append(NavItems[0]))
            {
                Nav(window, item).RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                });
                vm.CurrentPageKey.ShouldBe(item.PageKey, item.Name);
            }
        });
    });

    [Fact]
    public Task CompactNavigation_ShowsIconsOnlyAndKeepsToolTips() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(600, 440, show: true, (window, _) =>
        {
            Named<TextBlock>(window, "NavTitleText").Visibility.ShouldBe(Visibility.Collapsed);

            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                var (_, icon, label) = Parts(nav);

                // 48（导航列）− 6 − 6（导航外框的左右内边距）= 36；滚动条不得挤占这 36。
                nav.ActualWidth.ShouldBe(36d, 1d, item.Name);

                // 文字所在的列（内部 Grid 的第 1 列）被挤成 0 宽，文字靠布局裁剪消失。
                // 不断言 label.ActualWidth：被裁剪的元素，ActualWidth 仍是它「想要」的宽度，不一定是 0。
                Grid.GetColumn(label).ShouldBe(1, item.Name);
                nav.Child.ShouldBeOfType<Grid>().ColumnDefinitions[1].ActualWidth.ShouldBeLessThan(1d, item.Name);

                icon.IsVisible.ShouldBeTrue(item.Name);
                icon.ActualWidth.ShouldBeGreaterThan(0d, item.Name);
                icon.TransformToAncestor(nav).Transform(new Point(icon.ActualWidth, 0)).X.ShouldBeLessThanOrEqualTo(nav.ActualWidth + 0.5, item.Name);
                (nav.ToolTip as string).ShouldBe(item.Text(), item.Name);
            }
        });
    });

    [Fact]
    public Task WideNavigation_ShowsEveryLabelOnOneLine() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, _) =>
        {
            Named<TextBlock>(window, "NavTitleText").IsVisible.ShouldBeTrue();

            double firstHeight = Nav(window, NavItems[0]).ActualHeight;
            firstHeight.ShouldBeGreaterThan(0d);
            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                var label = Parts(nav).Label;
                Grid.GetColumn(label).ShouldBe(1, item.Name);
                nav.Child.ShouldBeOfType<Grid>().ColumnDefinitions[1].ActualWidth.ShouldBeGreaterThan(label.Margin.Left, item.Name);
                label.ActualWidth.ShouldBeGreaterThan(0d, item.Name);
                // 单行不折行：八项一样高。
                nav.ActualHeight.ShouldBe(firstHeight, 0.5, item.Name);
            }
        });
    });

    [Fact]
    public Task Footer_SaveAndCancelStayInsideTheMinimumWindow() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(600, 440, show: true, (window, _) =>
        {
            foreach (string content in new[] { UiText.SettingsCancel, UiText.SettingsSave })
            {
                var button = Descendants<Wpf.Ui.Controls.Button>(window).Single(b => Equals(b.Content, content));
                button.IsVisible.ShouldBeTrue(content);
                button.ActualWidth.ShouldBeGreaterThanOrEqualTo(70d, content);
                var bottomRight = button.TransformToAncestor(window).Transform(new Point(button.ActualWidth, button.ActualHeight));
                bottomRight.X.ShouldBeLessThanOrEqualTo(window.ActualWidth + 1d, content);
                bottomRight.Y.ShouldBeLessThanOrEqualTo(window.ActualHeight + 1d, content);
            }
        });
    });

    [Fact]
    public Task Skeleton_TintsTheRootAndLeavesNavigationAndFooterWithoutAnOpaqueFill() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, _) =>
        {
            try
            {
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
                var root = window.Content.ShouldBeOfType<Grid>();
                Color lightTint = ColorOf(window.FindResource("IrisAcrylicTintBrush"));
                root.Background.ShouldNotBeNull();
                ColorOf(root.Background).ShouldBe(lightTint);

                var navHost = NavHost(window);
                navHost.Background.ShouldBeNull();
                navHost.BorderThickness.ShouldBe(new Thickness(0));

                var footer = Footer(window);
                footer.Background.ShouldBeNull();
                footer.BorderThickness.ShouldBe(new Thickness(0, 1, 0, 0));
                ColorOf(footer.BorderBrush).ShouldBe(ColorOf(window.FindResource("IrisAcrylicCardStrokeBrush")));

                // 着色层与底栏上边线必须是 DynamicResource：换成深色字典后跟着变。
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                Color darkTint = ColorOf(window.FindResource("IrisAcrylicTintBrush"));
                darkTint.ShouldNotBe(lightTint);
                ColorOf(root.Background).ShouldBe(darkTint);
                ColorOf(footer.BorderBrush).ShouldBe(ColorOf(window.FindResource("IrisAcrylicCardStrokeBrush")));
            }
            finally
            {
                // 整个测试进程只有一个 Application：不还原，深色的 Iris 字典就留给后面的测试了。
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
            }
        });
    });
    [Fact]
    public Task DefaultWindowWithMouseStatus_FitsAllEightNavItemsWithoutScrollbar() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, vm) =>
        {
            var snapshot = new Iris.App.Selection.MouseSelectionRuntimeSnapshot(0, default, default, false, null, false);
            typeof(SettingsViewModel)
                .GetMethod("OnMouseRuntimeChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(vm, new object?[] { null, snapshot });
            window.UpdateLayout();

            var mouseStatus = Named<TextBlock>(window, "MouseRuntimeStatusTextBlock");
            mouseStatus.IsVisible.ShouldBeTrue();

            var navScroll = Named<Border>(window, "NavOcr").Parent.ShouldBeOfType<StackPanel>().Parent.ShouldBeOfType<ScrollViewer>();
            navScroll.ScrollableHeight.ShouldBe(0d);
            navScroll.ComputedVerticalScrollBarVisibility.ShouldBe(Visibility.Collapsed);

            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                var topLeft = nav.TransformToAncestor(navScroll).Transform(new Point(0, 0));
                topLeft.Y.ShouldBeGreaterThanOrEqualTo(-0.5, item.Name);
                (topLeft.Y + nav.ActualHeight).ShouldBeLessThanOrEqualTo(navScroll.ActualHeight + 0.5, item.Name);
            }
        });
    });
}
