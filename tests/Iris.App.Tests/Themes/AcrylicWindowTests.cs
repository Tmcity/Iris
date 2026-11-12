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
}
