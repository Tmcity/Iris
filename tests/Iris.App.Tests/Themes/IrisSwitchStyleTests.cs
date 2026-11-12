using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisSwitchStyleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SwitchStyle_RendersCheckedStateImmediately(bool isChecked) => WpfTestRunner.RunAsync(() =>
    {
        var checkBox = new CheckBox { Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox") };
        var host = new Window { Content = checkBox, Width = 300, Height = 60, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            checkBox.IsChecked = !isChecked;
            host.Show();
            checkBox.IsChecked = isChecked;
            host.UpdateLayout();
            var knob = (System.Windows.Shapes.Rectangle)checkBox.Template.FindName("ToggleKnob", checkBox);
            var transform = (System.Windows.Media.TranslateTransform)knob.RenderTransform;
            transform.X.ShouldBe(isChecked ? 9d : -9d);
            checkBox.MinWidth.ShouldBe(0d);
            checkBox.MinHeight.ShouldBe(0d);
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task SwitchStyle_KeepsWrappingContentTemplate() => WpfTestRunner.RunAsync(() =>
    {
        // SettingsWindow.xaml:16 的 WrappingCheckBoxContent 是**窗口级**资源，全局取不到，
        // 所以这里就地解析一个与它等价的模板。要验证的是自定义 ControlTemplate 是否仍然尊重
        // 调用方给的 ContentTemplate，与模板从哪来无关。
        var wrappingTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding}\" TextWrapping=\"Wrap\" /></DataTemplate>");
        var checkBox = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = wrappingTemplate,
            Content = "a long enough sentence to wrap onto a second line inside the row",
        };
        var host = new Window { Content = checkBox, Width = 200, Height = 80, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            var presenter = (ContentPresenter)checkBox.Template.FindName("ContentPresenter", checkBox);
            presenter.ActualWidth.ShouldBeGreaterThan(40);
            presenter.ContentTemplate.ShouldBeSameAs(wrappingTemplate);
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task SwitchStyle_OffStateUsesTheOffStateKeys() => WpfTestRunner.RunAsync(() =>
    {
        // 这三个键写在 ControlTemplate **体内**元素的属性上（Iris.Styles.xaml:96/97/99），不在任何
        // Setter.Value 上，所以 IrisStyleKeyResolutionTests 的遍历到不了它们：把键名打错，那条守卫
        // 照样全绿，而关态开关会变成没有底槽、没有描边的透明块。这里从消费端断言，顺便锁住"用的是
        // OFF 那一组键"而不是 ON 那一组。
        var checkBox = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("IrisSwitchCheckBox"),
            IsChecked = false,
        };
        var host = new Window { Content = checkBox, Width = 120, Height = 60, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show();
            host.UpdateLayout();
            var track = (System.Windows.Shapes.Rectangle)checkBox.Template.FindName("ToggleRectangle", checkBox);
            var knob = (System.Windows.Shapes.Rectangle)checkBox.Template.FindName("ToggleKnob", checkBox);
            track.Fill.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("ToggleSwitchFillOff"));
            track.Stroke.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("ToggleSwitchStrokeOff"));
            knob.Fill.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("ToggleSwitchKnobFillOff"));
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    // 拿已合并字典里那个键的值当基准，而不是写死颜色：断言的是"这个属性由那个键驱动"，
    // 颜色改了不用动测试。键名打错时 FindResource 会直接抛，比断 null 更响亮。
    private static Color BrushColor(string key) =>
        Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>().Color;
}
