using System.Threading.Tasks;
using System.Windows;
using Iris.App.History;
using Iris.App.Settings;
using Iris.App.Translation;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class AcrylicWindowTests
{
    [Fact]
    public Task SettingsWindow_ExtendsContentIntoTitleBarAndKeepsTheBackdropForTheService() => WpfTestRunner.RunAsync(() =>
    {
        AssertExtendsTitleBarAndLeftBackdropUnset(new SettingsWindow());   // 无参构造（SettingsWindow.xaml.cs:24）
        return Task.CompletedTask;
    });

    [Fact]
    public Task TranslateWindow_ExtendsContentIntoTitleBarAndKeepsTheBackdropForTheService() => WpfTestRunner.RunAsync(() =>
    {
        AssertExtendsTitleBarAndLeftBackdropUnset(new TranslateWindow());   // 无参构造（TranslateWindow.xaml.cs:15）
        return Task.CompletedTask;
    });

    [Fact]
    public Task HistoryWindow_ExtendsContentIntoTitleBarAndKeepsTheBackdropForTheService() => WpfTestRunner.RunAsync(() =>
    {
        // HistoryWindow 是唯一带必需参数的：HistoryWindow(IDesktopContext, INativeWindowPlacementAdapter)。
        // 不 Show()，所以替身不需要配置 GetMonitors()/GetCursorPosition()（对照 HistoryTestScope.cs:55-56）。
        AssertExtendsTitleBarAndLeftBackdropUnset(
            new HistoryWindow(Substitute.For<IDesktopContext>(), Substitute.For<INativeWindowPlacementAdapter>()));
        return Task.CompletedTask;
    });

    [Fact]
    public Task HistoryWindow_RootPanelCarriesTheAcrylicTint() => WpfTestRunner.RunAsync(() =>
    {
        var window = new HistoryWindow(Substitute.For<IDesktopContext>(), Substitute.For<INativeWindowPlacementAdapter>());
        AssertIsTheTintLayer(window.Content.ShouldBeAssignableTo<System.Windows.Controls.Panel>()!);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TranslateWindow_TintLayerCoversTheClientAreaAndLeavesTheLayoutAlone() => WpfTestRunner.RunAsync(() =>
    {
        var window = new TranslateWindow();
        var root = window.Content.ShouldBeAssignableTo<System.Windows.Controls.Panel>()!;
        AssertIsTheTintLayer(root);

        // 版面没动：原来的 DockPanel 原样待在着色层里，边距还是那四个数，自己不带底色。
        // 着色不能直接加在它身上——Background 只画在 Margin 以内，四周会留一圈没着色的边。
        var dock = root.Children.Cast<UIElement>().ShouldHaveSingleItem()
            .ShouldBeOfType<System.Windows.Controls.DockPanel>()!;
        dock.Margin.ShouldBe(new Thickness(12, 8, 12, 12));
        dock.Background.ShouldBeNull();
        return Task.CompletedTask;
    });

    private static void AssertExtendsTitleBarAndLeftBackdropUnset(FluentWindow window)
    {
        window.ExtendsContentIntoTitleBar.ShouldBeTrue();

        // "XAML 没设 WindowBackdropType" 的准确写法 == 它仍等于 DP 的默认值。不写死 None：
        // 默认值由 WPF-UI 决定、本机无法静态判定，若它其实是 Auto，写死 None 会把一份正确的
        // XAML 判红。设过它就会偏离默认值——这才是本阶段要守的那条线（材质归 WindowMaterialService）。
        var backdropDefault = (WindowBackdropType)FluentWindow.WindowBackdropTypeProperty
            .GetMetadata(typeof(FluentWindow)).DefaultValue!;
        window.WindowBackdropType.ShouldBe(backdropDefault);
    }

    private static void AssertIsTheTintLayer(System.Windows.Controls.Panel root)
    {
        // 着色层必须铺满客户区：带 Margin 的面板只会画出一块内嵌矩形，四周露出没着色的背板。
        root.Margin.ShouldBe(new Thickness(0));

        // 按「资源引用」断言，不比颜色值：共享 Application 里留的是浅色还是深色字典都不影响这条。
        // {DynamicResource} 落在依赖属性上时，本地值是资源引用表达式，不是画刷本身。
        var local = root.ReadLocalValue(System.Windows.Controls.Panel.BackgroundProperty);
        local.ShouldNotBe(DependencyProperty.UnsetValue, "the root panel has no Background");
        local.ShouldBeAssignableTo<System.Windows.Expression>("Background must be a {DynamicResource} reference, not a brush set in place");
        var reference = new System.Windows.Markup.ResourceReferenceExpressionConverter()
            .ConvertTo(local, typeof(System.Windows.Markup.MarkupExtension))
            .ShouldBeOfType<DynamicResourceExtension>()!;
        reference.ResourceKey.ShouldBe("IrisAcrylicTintBrush");

        // 键真的解析到了画刷：DynamicResource 拼错键名只会静默留空，不会抛异常。
        root.Background.ShouldNotBeNull();
        root.Background.ShouldBeSameAs(root.FindResource("IrisAcrylicTintBrush"));
    }
}
