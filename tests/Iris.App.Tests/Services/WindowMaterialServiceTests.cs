using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Services;

[Collection("IrisThemeResources")]
public sealed class WindowMaterialServiceTests
{
    [Theory]
    [InlineData(22621, true)]
    [InlineData(22631, true)]
    [InlineData(22620, false)]
    [InlineData(19045, false)]
    [InlineData(22000, false)]
    public Task IsAcrylicSupported_DependsOnOsBuild(int build, bool expected) => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => build, () => true);
        service.IsAcrylicSupported.ShouldBe(expected);
        return Task.CompletedTask;
    });

    [Fact]
    public Task IsAcrylicSupported_IsFalse_WhenBackdropReportsUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => false);
        service.IsAcrylicSupported.ShouldBeFalse();
        return Task.CompletedTask;
    });

    [Fact]
    public Task ApplyAcrylic_SetsBackdropType_WhenSupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            service.ApplyAcrylic(window);
            window.WindowBackdropType.ShouldBe(WindowBackdropType.Acrylic);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ApplyAcrylic_LeavesWindowSolid_WhenUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 19045, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            service.ApplyAcrylic(window);
            window.WindowBackdropType.ShouldBe(BackdropDefault());
            var background = window.Background.ShouldBeOfType<SolidColorBrush>();
            background.Color.A.ShouldBe((byte)255);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Attach_AppliesBackdrop_AfterTheHandleIsCreated() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            service.Attach(window);                                   // 先挂，后 Show：此时还没有句柄
            window.WindowBackdropType.ShouldBe(BackdropDefault());
            window.Show();                                            // 句柄创建 → SourceInitialized → 应用
            window.WindowBackdropType.ShouldBe(WindowBackdropType.Acrylic);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Attach_DoesNothing_WhenUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 19045, () => true);
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            service.Attach(window);
            window.Show();
            window.WindowBackdropType.ShouldBe(BackdropDefault());
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    // 回归：切主题会拆掉 DWM 背板、并把 Background 重置成不透明色（实测
    // ApplicationThemeManager.Apply(theme, WindowBackdropType.None) 之后 DWMWA_SYSTEMBACKDROP_TYPE
    // 从 3 变 1），所以之后必须把**背板**也装回来——只断言 Background 的 alpha 会在"窗口其实已经
    // 没有亚克力"时照绿。MainWindow 显式设成本窗口：Apply 只作用于 Application.MainWindow，
    // 显式指定让这条用例与执行顺序、与同进程里别的测试是否先建过窗口都无关。
    [Fact]
    public Task ApplyAcrylic_RestoresBackdrop_AfterTheThemeChangeRemovedIt() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current.MainWindow;
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            Application.Current.MainWindow = window;
            service.ApplyAcrylic(window);
            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None);

            // 前置断言：先把「主题切换确实把背板拆掉了」钉死，堵住「中间这步被短路」的假绿。
            // 注意：在根本产不出背板的环境里（dwm 读回一直是 1），这条会真空通过，红的落点变成下面的
            // 终态断言——两种情形都不会假绿。
            SystemBackdropType(window).ShouldNotBe(AcrylicBackdropType);

            service.ApplyAcrylic(window);

            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
            SystemBackdropType(window).ShouldBe(AcrylicBackdropType);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
        }

        return Task.CompletedTask;
    });

    private const int DwmwaSystemBackdropType = 38;
    private const int AcrylicBackdropType = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // 读 DWM 的 SYSTEMBACKDROP_TYPE：3 = 亚克力。hr 不为 0 表示这次读回无效，直接判红。
    // 注意：这条断言依赖真实系统能上亚克力（Win11 + 透明效果打开），与那 10 条只读 DP 的用例不同。
    private static int SystemBackdropType(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        DwmGetWindowAttribute(handle, DwmwaSystemBackdropType, out var value, sizeof(int)).ShouldBe(0);
        return value;
    }

    // "服务没碰过它" == 该属性仍等于 DP 的默认值。不写死 None：默认值由 WPF-UI 决定、
    // 本机无法静态判定（若它其实是 Auto，写死 None 会把一份正确的行为判红）。
    // 写成方法而不是静态字段：字段初始化可能落在 xunit 的非 STA 线程上。
    private static WindowBackdropType BackdropDefault() => (WindowBackdropType)
        FluentWindow.WindowBackdropTypeProperty.GetMetadata(typeof(FluentWindow)).DefaultValue!;

    [Fact]
    public Task RefreshOpenWindows_RestoresTheBackdropAfterThemeChange() => WpfTestRunner.RunAsync(() =>
    {
        var service = new WindowMaterialService(() => 26100, () => true);
        // 必须在建窗口之前读：MainWindow 为空时，WPF 会把新建的第一个窗口自动设成 MainWindow。
        var previousMainWindow = Application.Current.MainWindow;
        var window = new FluentWindow { ExtendsContentIntoTitleBar = true, Content = "placeholder" };
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            // Apply(theme, None) 只作用于 Application.MainWindow。不显式指定的话，这条用例拆的可能是
            // 别的窗口，前置断言就跟着失效——整条用例会退化成空转。指定之后，
            // 它与同类的其它用例、与执行顺序都无关。
            Application.Current.MainWindow = window;
            service.ApplyAcrylic(window);
            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None);

            // 前置断言：钉死「切主题确实把背板拆掉了」。在根本产不出背板的环境里它会真空通过，
            // 那时红的落点是下面的终态断言——两种情形都不会假绿。
            SystemBackdropType(window).ShouldNotBe(AcrylicBackdropType);
            window.Background.ShouldBeOfType<SolidColorBrush>()
                .Color.A.ShouldBe((byte)255, "the theme change is expected to restore an opaque background");

            service.RefreshOpenWindows();

            // 终态：背景透明**和**背板都得在。只断言 alpha 会在「窗口其实已经没有亚克力」时照绿。
            SystemBackdropType(window).ShouldBe(AcrylicBackdropType);
            window.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
        }
        finally
        {
            Application.Current.MainWindow = previousMainWindow;
            window.Close();
        }

        return Task.CompletedTask;
    });
}
