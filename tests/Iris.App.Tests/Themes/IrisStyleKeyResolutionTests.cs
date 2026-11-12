using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Themes;

[Collection("IrisThemeResources")]
public sealed class IrisStyleKeyResolutionTests
{
    // 从已加载的 Style 对象图里挖出每一个写在 Setter 上的 {DynamicResource X} 键（覆盖边界见下），
    // 再逐个问 Application 解不解得开。
    // 不读源码文本：页面编译成 BAML，GetResourceStream 拿到的是 BAML 流而不是 XAML 文本，
    // 在测试里对源码做正则解析做不到。
    //
    // **覆盖边界（别把它当"整份文件全覆盖"）**：只到 Setter.Value 为止——Style.Setters、
    // Style.Triggers，以及 ControlTemplate.Triggers 里带/不带 TargetName 的 Setter 都收；
    // 写在模板**体内**元素属性上的引用收不到（模板实例化前它们在 BAML 的延迟内容里），
    // 本文件里就是这样三处：Iris.Styles.xaml:96/97/99 的 ToggleSwitchFillOff /
    // ToggleSwitchStrokeOff / ToggleSwitchKnobFillOff。那三个键由
    // IrisSwitchStyleTests.SwitchStyle_OffStateUsesTheOffStateKeys 从消费端显式断言。
    // 于是本守卫当前覆盖 29 个键里的 26 个；将来谁往模板体里加引用，要照上面那条补断言。
    [Fact]
    public Task EveryDynamicResourceKeyUsedByIrisStylesResolves() => WpfTestRunner.RunAsync(() =>
    {
        var styles = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.Styles.xaml", UriKind.Absolute)
        };
        var used = new List<string>();
        foreach (var entry in styles.Keys)
        {
            if (styles[entry] is Style style)
            {
                CollectKeys(style, used);
            }
        }

        // 防空转：挖到 0 个键的话下面的循环一条都不跑，"全部通过"就是假的。若这条红了，说明
        // Setter.Value 上 {DynamicResource} 没有保持为 DynamicResourceExtension（本写法依赖它
        // 保持原样——Setter.Value 不是 DependencyProperty 目标，所以 ProvideValue 返回扩展本身）。
        used.ShouldNotBeEmpty();

        foreach (var key in used.Distinct())
        {
            Application.Current.TryFindResource(key).ShouldNotBeNull(
                $"{key} 被 Themes/Iris.Styles.xaml 使用，但没有任何已合并的字典定义它");
        }

        return Task.CompletedTask;
    });

    private static void CollectKeys(Style style, List<string> keys)
    {
        foreach (var setter in style.Setters.OfType<Setter>())
        {
            AddIfDynamicResource(setter.Value, keys);
            if (setter.Value is ControlTemplate template)
            {
                foreach (var trigger in template.Triggers)
                {
                    CollectTriggerKeys(trigger, keys);
                }
            }
        }

        foreach (var trigger in style.Triggers)
        {
            CollectTriggerKeys(trigger, keys);
        }
    }

    private static void CollectTriggerKeys(TriggerBase trigger, List<string> keys)
    {
        IEnumerable<Setter> setters = trigger switch
        {
            Trigger single => single.Setters.OfType<Setter>(),
            MultiTrigger multi => multi.Setters.OfType<Setter>(),
            DataTrigger data => data.Setters.OfType<Setter>(),
            MultiDataTrigger multiData => multiData.Setters.OfType<Setter>(),
            // EventTrigger 没有 Setter，本来就没得收。至于认不出来的触发器类型：静默返回空集合
            // 会让"新触发器类型带了个键"变成无声缺口，所以直接报红，逼人回来更新这个 switch。
            EventTrigger => Enumerable.Empty<Setter>(),
            _ => throw new NotSupportedException(
                $"{trigger.GetType().FullName} 是本守卫不认识的触发器类型，请更新 IrisStyleKeyResolutionTests"),
        };
        foreach (var setter in setters)
        {
            AddIfDynamicResource(setter.Value, keys);
        }
    }

    private static void AddIfDynamicResource(object? value, List<string> keys)
    {
        if (value is not DynamicResourceExtension extension)
        {
            return;
        }

        // ResourceKey 声明成 object。非字符串的键（ComponentResourceKey、{x:Type} 之类）没法拿去
        // TryFindResource，静默跳过又是一个无声缺口，所以让 Shouldly 当场报红。
        keys.Add(extension.ResourceKey.ShouldBeOfType<string>());
    }
}
