using System.Threading.Tasks;
using System.Windows;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisStyleResourceTests
{
    // 只覆盖被样式真正消费的 token。Type 就是这些 token 的全部契约，所以断言类型；
    // 不断言具体数值——那等于把 XAML 抄第二遍。IrisPillCornerRadius / IrisCardGap /
    // IrisControlGap 本阶段无消费者，故不在此列。
    [Theory]
    [InlineData("IrisCardCornerRadius")]
    [InlineData("IrisRowCornerRadius")]
    [InlineData("IrisTagCornerRadius")]
    public Task CornerRadiusResources_AreDefined(string key) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<CornerRadius>();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IrisCardPadding")]
    [InlineData("IrisRowPadding")]
    [InlineData("IrisRowGap")]
    public Task ThicknessResources_AreDefined(string key) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<Thickness>();
        return Task.CompletedTask;
    });
}
