using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisColorKeyTests
{
    [Theory]
    [InlineData("IrisAcrylicCardFillBrush")]
    [InlineData("IrisAcrylicCardStrokeBrush")]
    [InlineData("IrisAcrylicRowFillBrush")]
    [InlineData("IrisAcrylicRowStrokeBrush")]
    public Task IrisColorKeys_AreTranslucent(string key) => WpfTestRunner.RunAsync(() =>
    {
        var light = Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>();
        light.Color.A.ShouldBeLessThan((byte)255);
        // 深色字典也必须自己验值，不能只靠下面那条"键名与数量"的覆盖：把 Iris.Dark.xaml 里某个键
        // 改成不透明，或 alpha 打错一位（26 写成 13），9 个用例会全绿。
        var dark = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Dark.xaml", UriKind.Absolute) };
        dark[key].ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBeLessThan((byte)255);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IrisAcrylicCardFillBrush")]
    [InlineData("IrisAcrylicCardStrokeBrush")]
    [InlineData("IrisAcrylicRowFillBrush")]
    [InlineData("IrisAcrylicRowStrokeBrush")]
    public Task SolidFallbackKeys_AreOpaque(string key) => WpfTestRunner.RunAsync(() =>
    {
        var fallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml", UriKind.Absolute) };
        var brush = fallback[key].ShouldBeOfType<SolidColorBrush>();
        brush.Color.A.ShouldBe((byte)255);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SolidFallback_OverridesEveryIrisColorKey() => WpfTestRunner.RunAsync(() =>
    {
        var light = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Light.xaml", UriKind.Absolute) };
        var dark = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Dark.xaml", UriKind.Absolute) };
        var fallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml", UriKind.Absolute) };
        // Source 加载的字典取 Keys 得到的是基础字典的快照（ResourceDictionary.Keys 的 Remarks）。
        // 若快照为空，下面的循环会一条都不跑而假通过，所以先断非空——这两行不能删。
        var lightKeys = light.Keys.Cast<object>().ToList();
        var darkKeys = dark.Keys.Cast<object>().ToList();
        lightKeys.ShouldNotBeEmpty();
        darkKeys.ShouldNotBeEmpty();
        lightKeys.Count.ShouldBe(darkKeys.Count);
        // 数量相等本身不够——两份字典键名不同但数量相同也能过。这里显式断言"深色的每个键名
        // 浅色也有"（配上数量相等即集合相等）。下面 fallback.Keys.Count 那条与两个 foreach
        // 连在一起其实也恰好能推出集合相等，但那个保证藏在三条断言之间、读代码看不出来，
        // 删掉任意一条就静默消失，所以这里把它写明白。
        foreach (var key in darkKeys)
        {
            lightKeys.ShouldContain(key);
        }

        foreach (var key in lightKeys)
        {
            fallback.Contains(key).ShouldBeTrue($"{key} is missing from Iris.SolidFallback.xaml");
        }

        foreach (var key in darkKeys)
        {
            fallback.Contains(key).ShouldBeTrue($"{key} is missing from Iris.SolidFallback.xaml");
        }

        fallback.Keys.Count.ShouldBe(lightKeys.Count);
        return Task.CompletedTask;
    });
}
