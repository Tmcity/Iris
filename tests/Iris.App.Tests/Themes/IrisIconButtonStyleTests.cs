using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Wpf.Ui.Controls;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisIconButtonStyleTests
{
    [Fact]
    public Task IconButton_RendersIconOnlyAndKeepsStringContent() => WpfTestRunner.RunAsync(async () =>
    {
        var icon = new SymbolIcon { Symbol = SymbolRegular.Copy24 };
        var button = new Wpf.Ui.Controls.Button
        {
            Style = (Style)Application.Current.FindResource("IrisIconButton"),
            Content = "CopySourceFull",
            Icon = icon,
        };
        var host = new Window { Content = button, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            button.Content.ShouldBe("CopySourceFull");
            button.ToolTip.ShouldBe("CopySourceFull");
            AutomationProperties.GetName(button).ShouldBe("CopySourceFull");
            button.ActualWidth.ShouldBe(32d);
            button.ActualHeight.ShouldBe(32d);
            // 契约是"界面只画 Icon"。少了下面这几条，把 Template 换回默认的（会画 Content 文字）
            // 也照样能过——那时按钮里会出现字符串，而这条测试必须挡住它。
            // TextBlock 必须写全限定名：本文件同时 using 了 Wpf.Ui.Controls 与 System.Windows.Controls，
            // 两个命名空间都有 TextBlock，裸写会 CS0104 编译失败。写成基类 System.Windows.Controls.TextBlock
            // 同时也是更宽的覆盖——默认模板渲染字符串生成的就是它。
            var presenter = button.Template.FindName("ControlIcon", button).ShouldBeOfType<ContentPresenter>();
            presenter.Content.ShouldBeSameAs(icon);
            presenter.ActualWidth.ShouldBeGreaterThan(0d);
            // 下面这条防空转断言不能删：ShouldNotContain 只有在树里确实存在 TextBlock 时才验得动
            // （默认模板渲染字符串生成的正是它）。若 WPF-UI 哪天换掉文本画法，ShouldNotContain 会
            // 静默恒真、测试照样绿；这条会报红，逼人更新测试，而不是让守护悄悄失效。
            Descendants<System.Windows.Controls.TextBlock>(button).ShouldNotBeEmpty();
            Descendants<System.Windows.Controls.TextBlock>(button).ShouldNotContain(text => text.Text == "CopySourceFull");
            var clickCount = 0;
            button.Click += (_, _) => clickCount++;
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            // ButtonAutomationPeer.Invoke() 用 Dispatcher.BeginInvoke(Input) 异步派发 Click；
            // 这里必须先把队列里优先级高于 Background 的操作跑完，否则断言读到的是旧值。
            await button.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            clickCount.ShouldBe(1);
            // 这条守护第一轮修掉的那个 bug：按下反馈必须只以 IsPressed 为条件。改回
            // IsMouseOver+IsPressed 的话，Tab 聚焦后按空格就没有任何按下反馈，
            // 而上面所有断言仍然全绿——所以必须直接断模板结构。
            button.Template.Triggers.OfType<Trigger>().Any(t => t.Property == ButtonBase.IsPressedProperty).ShouldBeTrue();
        }
        finally
        {
            host.Close();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
