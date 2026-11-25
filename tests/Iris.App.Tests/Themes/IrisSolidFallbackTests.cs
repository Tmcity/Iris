using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Appearance;
using Xunit;

namespace Iris.App.Tests.Themes;

// 后三条用例会改 Application.Current.Resources.MergedDictionaries，所以放进不并行的集合；
// 改了全局资源的用例都在 finally 里调 Cleanup()。
[Collection("IrisThemeResources")]
public sealed class IrisSolidFallbackTests
{
    private const string PackRoot = "pack://application:,,,/Iris.App;component/";
    private const string LightFile = "Themes/Iris.Light.xaml";
    private const string DarkFile = "Themes/Iris.Dark.xaml";
    private const string FallbackLightFile = "Themes/Iris.SolidFallback.xaml";
    private const string FallbackDarkFile = "Themes/Iris.SolidFallback.Dark.xaml";
    private const string CardFillKey = "IrisAcrylicCardFillBrush";

    // 四份字典都恰好是这五个键（按序数排序后用逗号连起来）。
    private const string ExpectedKeys =
        "IrisAcrylicCardFillBrush,IrisAcrylicCardStrokeBrush,IrisAcrylicRowFillBrush,IrisAcrylicRowStrokeBrush,IrisAcrylicTintBrush";

    [Fact]
    public Task AllFourColorDictionaries_HaveTheSameKeys() => WpfTestRunner.RunAsync(() =>
    {
        // 每一份都对着写死的五个键比，而不是四份互相比：四份一起少一个键时「两两相同」照样成立。
        SortedKeys(LightFile).ShouldBe(ExpectedKeys);
        SortedKeys(DarkFile).ShouldBe(ExpectedKeys);
        SortedKeys(FallbackLightFile).ShouldBe(ExpectedKeys);
        SortedKeys(FallbackDarkFile).ShouldBe(ExpectedKeys);
        return Task.CompletedTask;
    });

    // 值逐个钉住：深色回退色是算出来的合成色，错一位肉眼看不出来。
    [Theory]
    [InlineData(LightFile, "IrisAcrylicTintBrush", 0xB3F3F3F3u)]
    [InlineData(DarkFile, "IrisAcrylicTintBrush", 0xAD242424u)]
    [InlineData(FallbackLightFile, "IrisAcrylicTintBrush", 0xFFF3F3F3u)]
    [InlineData(FallbackLightFile, "IrisAcrylicCardFillBrush", 0xFFFFFFFFu)]
    [InlineData(FallbackLightFile, "IrisAcrylicCardStrokeBrush", 0xFFE0E0E0u)]
    [InlineData(FallbackLightFile, "IrisAcrylicRowFillBrush", 0xFFF9F9F9u)]
    [InlineData(FallbackLightFile, "IrisAcrylicRowStrokeBrush", 0xFFE5E5E5u)]
    [InlineData(FallbackDarkFile, "IrisAcrylicTintBrush", 0xFF242424u)]
    [InlineData(FallbackDarkFile, "IrisAcrylicCardFillBrush", 0xFF3A3A3Au)]
    [InlineData(FallbackDarkFile, "IrisAcrylicCardStrokeBrush", 0xFF3F3F3Fu)]
    [InlineData(FallbackDarkFile, "IrisAcrylicRowFillBrush", 0xFF353535u)]
    [InlineData(FallbackDarkFile, "IrisAcrylicRowStrokeBrush", 0xFF393939u)]
    public Task ColorDictionary_HoldsTheDecidedValue(string file, string key, uint argb) => WpfTestRunner.RunAsync(() =>
    {
        Load(file)[key].ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(ToColor(argb));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(FallbackLightFile)]
    [InlineData(FallbackDarkFile)]
    public Task SolidFallbackDictionary_IsFullyOpaque(string file) => WpfTestRunner.RunAsync(() =>
    {
        var dictionary = Load(file);
        var keys = dictionary.Keys.Cast<object>().ToList();
        // 防空转：键快照为空时下面的循环一条都不跑。
        keys.ShouldNotBeEmpty();
        foreach (var key in keys)
        {
            dictionary[key].ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)255, $"{key} in {file}");
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ApplyIrisSolidFallbackDictionary_MergesSwapsAndRemoves() => WpfTestRunner.RunAsync(() =>
    {
        try
        {
            // 起点：浅色主题字典在场、没有回退字典、键取到半透明值。少了这几句，后面的断言可能恒真。
            Cleanup();
            ThemeDictionaryIndex().ShouldBeGreaterThanOrEqualTo(0);
            FallbackSources().ShouldBeEmpty();
            CurrentCardFill().ShouldBe(ToColor(0x66FFFFFFu));

            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Dark, acrylicSupported: false);

            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackDarkFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFF3A3A3Au));
            // 位置决定覆盖优先级：必须紧跟在 Iris 主题字典后面。
            FallbackIndex().ShouldBe(ThemeDictionaryIndex() + 1);

            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Light, acrylicSupported: false);

            // 恰好一份：换主题不能把旧的那份留下。
            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackLightFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFFFFFFFFu));
            FallbackIndex().ShouldBe(ThemeDictionaryIndex() + 1);

            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Light, acrylicSupported: true);

            FallbackSources().ShouldBeEmpty();
            CurrentCardFill().ShouldBe(ToColor(0x66FFFFFFu));
        }
        finally
        {
            Cleanup();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_ThroughThemeService_MergesTheFallbackWhenAcrylicIsUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        // ThemeService.Apply(AppTheme) 忽略传入值、改用设置里的主题（见 IrisThemeDictionaryTests），
        // 所以主题写进设置；传 Light 却出现深色回退值，才证明走的是整条路径。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Dark } });
        var unsupported = new WindowMaterialService(() => 19045, () => false);
        using var service = new ThemeService(settings, visualThemeApplier: _ => { }, windowMaterialService: unsupported);
        try
        {
            Cleanup();
            unsupported.IsAcrylicSupported.ShouldBeFalse();
            FallbackSources().ShouldBeEmpty();

            service.Apply(AppTheme.Light);

            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackDarkFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFF3A3A3Au));
            FallbackIndex().ShouldBe(ThemeDictionaryIndex() + 1);

            // 跟着主题切换走：设置改成浅色再应用一次，回退字典换成浅色那份，仍然恰好一份。
            settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
            service.Apply(AppTheme.Dark);

            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackLightFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFFFFFFFFu));
        }
        finally
        {
            Cleanup();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_ThroughThemeService_RemovesTheFallbackWhenThereIsNoMaterialService() => WpfTestRunner.RunAsync(() =>
    {
        // 没有材质服务（多数测试如此）按「支持亚克力」处理：回退字典不该在场。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        using var service = new ThemeService(settings, visualThemeApplier: _ => { });
        try
        {
            Cleanup();
            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Light, acrylicSupported: false);
            // 前置：回退字典确实在场，否则「应用之后没有」什么也证明不了。
            FallbackSources().ShouldHaveSingleItem();

            service.Apply(AppTheme.Light);

            FallbackSources().ShouldBeEmpty();
            CurrentCardFill().ShouldBe(ToColor(0x66FFFFFFu));
        }
        finally
        {
            Cleanup();
        }

        return Task.CompletedTask;
    });

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri(PackRoot + file, UriKind.Absolute) };

    private static string SortedKeys(string file) =>
        string.Join(
            ",",
            Load(file).Keys.Cast<object>().Select(key => key.ShouldBeOfType<string>()).OrderBy(key => key, StringComparer.Ordinal));

    private static Color ToColor(uint argb) =>
        Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

    private static Color CurrentCardFill() =>
        Application.Current!.Resources[CardFillKey].ShouldBeOfType<SolidColorBrush>().Color;

    private static string[] MergedSources() =>
        Application.Current!.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source?.OriginalString ?? string.Empty)
            .ToArray();

    private static bool IsFallback(string source) =>
        source.EndsWith(FallbackLightFile, StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(FallbackDarkFile, StringComparison.OrdinalIgnoreCase);

    private static bool IsThemeDictionary(string source) =>
        source.EndsWith(LightFile, StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(DarkFile, StringComparison.OrdinalIgnoreCase);

    private static string[] FallbackSources() => MergedSources().Where(IsFallback).ToArray();

    private static int FallbackIndex() => Array.FindIndex(MergedSources(), IsFallback);

    private static int ThemeDictionaryIndex() => Array.FindIndex(MergedSources(), IsThemeDictionary);

    // 清理不借用被测方法：它要是删不干净，回退字典就漏给后面的测试了。
    // 整个测试进程只有一个 Application，所以同时把 Iris 主题字典还原成浅色。
    private static void Cleanup()
    {
        var dictionaries = Application.Current!.Resources.MergedDictionaries;
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (IsFallback(dictionaries[index].Source?.OriginalString ?? string.Empty))
            {
                dictionaries.RemoveAt(index);
            }
        }

        ThemeService.ApplyIrisThemeDictionary(ApplicationTheme.Light);
    }
}
