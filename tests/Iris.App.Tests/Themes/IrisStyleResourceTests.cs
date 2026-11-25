using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    [InlineData("IrisPagePadding")]
    public Task ThicknessResources_AreDefined(string key) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<Thickness>();
        return Task.CompletedTask;
    });

    // 以下是阶段 2 的新键。这里断言了具体数值，与上面「不断言数值」的口径不同：
    // 176 / 48 / 24,16,24,16 是设计定下的版面数字，代码隐藏与各页的宽度预算都按它们算。
    [Theory]
    [InlineData("IrisNavWidth", 212d)]
    [InlineData("IrisNavCompactWidth", 48d)]
    public Task NavWidthResources_HoldTheDesignValues(string key, double expected) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<double>().ShouldBe(expected);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PagePadding_HoldsTheDesignValue() => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource("IrisPagePadding").ShouldBeOfType<Thickness>().ShouldBe(new Thickness(24, 16, 24, 16));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IrisPageTitle", typeof(TextBlock))]
    [InlineData("IrisRowIcon", typeof(Wpf.Ui.Controls.SymbolIcon))]
    [InlineData("IrisStatusText", typeof(TextBlock))]
    [InlineData("IrisRowError", typeof(TextBlock))]
    [InlineData("IrisListRowItem", typeof(ListBoxItem))]
    [InlineData("IrisDragHandle", typeof(Border))]
    [InlineData("IrisBreadcrumbButton", typeof(Wpf.Ui.Controls.Button))]
    [InlineData("IrisBreadcrumbCurrentText", typeof(TextBlock))]
    public Task NewStyles_AreDefinedForTheirTargetType(string key, Type targetType) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<Style>().TargetType.ShouldBe(targetType);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NavItem_UsesTheCompactPadding() => WpfTestRunner.RunAsync(() =>
    {
        // 8 + 20（图标）+ 8 = 36：窄态导航项正好放下一个图标，项高也是 36（S0-2）。
        new Border { Style = Find("IrisNavItem") }.Padding.ShouldBe(new Thickness(8));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TitleStyles_ShareOneType_LargerThanTheSectionHeader() => WpfTestRunner.RunAsync(() =>
    {
        var sectionHeader = new TextBlock { Style = Find("IrisSectionHeader") };
        var pageTitle = new TextBlock { Style = Find("IrisPageTitle") };
        var current = new TextBlock { Style = Find("IrisBreadcrumbCurrentText") };
        var back = new Wpf.Ui.Controls.Button { Style = Find("IrisBreadcrumbButton") };

        pageTitle.FontSize.ShouldBe(20d);
        pageTitle.FontSize.ShouldBeGreaterThan(sectionHeader.FontSize);
        pageTitle.FontWeight.ShouldBe(FontWeights.SemiBold);
        pageTitle.Margin.ShouldBe(Application.Current.FindResource("IrisCardGap").ShouldBeOfType<Thickness>());

        // 面包屑的两段与页标题同字号同字重：进出详情时标题不跳。
        current.FontSize.ShouldBe(pageTitle.FontSize);
        current.FontWeight.ShouldBe(pageTitle.FontWeight);
        back.FontSize.ShouldBe(pageTitle.FontSize);
        back.FontWeight.ShouldBe(pageTitle.FontWeight);

        current.TextTrimming.ShouldBe(TextTrimming.CharacterEllipsis);
        current.TextWrapping.ShouldBe(TextWrapping.NoWrap);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RowIcon_StatusText_DragHandle_HoldTheirMetrics() => WpfTestRunner.RunAsync(() =>
    {
        var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Settings20, Style = Find("IrisRowIcon") };
        icon.FontSize.ShouldBe(20d);
        icon.Margin.ShouldBe(new Thickness(0, 0, 12, 0));
        icon.VerticalAlignment.ShouldBe(VerticalAlignment.Center);

        var status = new TextBlock { Style = Find("IrisStatusText") };
        status.FontSize.ShouldBe(12d);
        status.TextWrapping.ShouldBe(TextWrapping.Wrap);

        var handle = new Border { Style = Find("IrisDragHandle") };
        handle.Width.ShouldBe(20d);
        handle.Cursor.ShouldBe(Cursors.SizeAll);
        // 背景必须是「透明画刷」而不是 null：null 背景命中不到，拖不动。
        handle.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RowError_CollapsesWhileEmpty_AndKeepsTheBrushSetOnTheElement() => WpfTestRunner.RunAsync(() =>
    {
        // C5：行内校验错误文字。空串时不占位；有文字时可见；样式本身不设前景色。
        var description = new TextBlock { Style = Find("IrisRowDescription") };
        var error = new TextBlock { Style = Find("IrisRowError") };

        error.Text.ShouldBe(string.Empty);
        error.Visibility.ShouldBe(Visibility.Collapsed);
        error.Text = "Invalid value";
        error.Visibility.ShouldBe(Visibility.Visible);
        error.Text = string.Empty;
        error.Visibility.ShouldBe(Visibility.Collapsed);

        // 字号与换行继承自 IrisRowDescription。
        Find("IrisRowError").BasedOn.ShouldBeSameAs(Find("IrisRowDescription"));
        error.FontSize.ShouldBe(description.FontSize);
        error.TextWrapping.ShouldBe(TextWrapping.Wrap);

        // 样式自己不设 Foreground：各行把原来的画刷留在元素上，样式不能与它抢。
        Find("IrisRowError").Setters.OfType<Setter>().ShouldNotContain(setter => setter.Property == TextBlock.ForegroundProperty);
        var brush = new SolidColorBrush(Colors.Red);
        var colored = new TextBlock { Style = Find("IrisRowError"), Foreground = brush, Text = "Invalid value" };
        colored.Foreground.ShouldBeSameAs(brush);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SecondaryTextStyles_ResolveTheSecondaryBrush_AndTheBreadcrumbIsATextButton() => WpfTestRunner.RunAsync(() =>
    {
        var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Settings20, Style = Find("IrisRowIcon") };
        var status = new TextBlock { Text = "Status", Style = Find("IrisStatusText") };
        var back = new Wpf.Ui.Controls.Button { Content = "Translation", Style = Find("IrisBreadcrumbButton") };
        var host = new Window
        {
            Content = new StackPanel { Children = { icon, status, back } },
            Width = 300,
            Height = 200,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        try
        {
            host.Show();
            host.UpdateLayout();

            var secondary = ColorOf("TextFillColorSecondaryBrush");
            // 前置：主色与次要色确实不同，否则下面三条分不出「套上了次要色」和「什么都没套」。
            secondary.ShouldNotBe(ColorOf("TextFillColorPrimaryBrush"));
            icon.Foreground.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(secondary);
            status.Foreground.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(secondary);
            back.Foreground.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(secondary);
            icon.ActualWidth.ShouldBeGreaterThan(0d);

            // 面包屑是「文字按钮」：画出 Content 的文字（与只画图标的 IrisIconButton 相反），无底无边。
            Descendants<TextBlock>(back).ShouldContain(text => text.Text == "Translation");
            back.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
            back.BorderThickness.ShouldBe(new Thickness(0));
            back.Padding.ShouldBe(new Thickness(0));
            // 键盘可达，且有焦点框。FindResource 在键不存在时会抛异常——这同时是对这个 WPF-UI 键的核实。
            back.Focusable.ShouldBeTrue();
            back.FocusVisualStyle.ShouldBeSameAs(Application.Current.FindResource("DefaultControlFocusVisualStyle"));
            // 悬停要变色：直接断模板结构，离屏测试里造不出真实的鼠标悬停。
            back.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsMouseOverProperty).ShouldBeTrue();
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task IrisCompactCard_ResolvesInLightAndDarkThemes() => WpfTestRunner.RunAsync(() =>
    {
        var style = Find("IrisCompactCard");
        style.TargetType.ShouldBe(typeof(Border));
        style.BasedOn.ShouldBeSameAs(Find("IrisCard"));

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);

            var lightBorder = new Border { Style = style };
            lightBorder.Padding.ShouldBe(new Thickness(12, 10, 12, 10));
            var lightFill = lightBorder.Background.ShouldBeOfType<SolidColorBrush>().Color;
            var lightStroke = lightBorder.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color;
            lightFill.ShouldBe(ColorOf("IrisAcrylicCardFillBrush"));
            lightStroke.ShouldBe(ColorOf("IrisAcrylicCardStrokeBrush"));

            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Dark);

            var darkBorder = new Border { Style = style };
            darkBorder.Padding.ShouldBe(new Thickness(12, 10, 12, 10));
            var darkFill = darkBorder.Background.ShouldBeOfType<SolidColorBrush>().Color;
            var darkStroke = darkBorder.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color;
            darkFill.ShouldBe(ColorOf("IrisAcrylicCardFillBrush"));
            darkStroke.ShouldBe(ColorOf("IrisAcrylicCardStrokeBrush"));

            darkFill.ShouldNotBe(lightFill);
        }
        finally
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task IrisGhostComboBox_ResolvesInLightAndDarkThemes() => WpfTestRunner.RunAsync(() =>
    {
        var style = Find("IrisGhostComboBox");
        style.TargetType.ShouldBe(typeof(ComboBox));
        style.BasedOn.ShouldBeSameAs(Application.Current.FindResource(typeof(ComboBox)));

        try
        {
            foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(theme);

                var combo = new ComboBox { Style = style };
                combo.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
                combo.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
                combo.BorderThickness.ShouldBe(new Thickness(0));
                combo.MinHeight.ShouldBe(28d);
                combo.Padding.ShouldBe(new Thickness(8, 4, 6, 4));
                combo.FontSize.ShouldBe(12.5d);
                combo.VerticalContentAlignment.ShouldBe(VerticalAlignment.Center);

                var host = new Window
                {
                    Content = combo,
                    Width = 240,
                    Height = 120,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                };
                try
                {
                    host.Show();
                    host.UpdateLayout();

                    var contentBorder = Descendants<Border>(combo).First(b => b.Name == "ContentBorder");
                    (contentBorder.Background as SolidColorBrush)?.Color.A.ShouldBe((byte)0);
                    (contentBorder.BorderBrush as SolidColorBrush)?.Color.A.ShouldBe((byte)0);

                    combo.IsDropDownOpen = true;
                    host.UpdateLayout();

                    var openFill = contentBorder.Background.ShouldBeOfType<SolidColorBrush>().Color;
                    openFill.A.ShouldBeGreaterThan((byte)0);

                    combo.IsDropDownOpen = false;
                }
                finally
                {
                    host.Close();
                }
            }
        }
        finally
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task IrisBorderlessTextBox_ResolvesTransparentBorderlessInLightAndDarkThemes() => WpfTestRunner.RunAsync(() =>
    {
        var themeField = typeof(Wpf.Ui.Appearance.ApplicationThemeManager)
            .GetField("_currentApplicationTheme", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var previousTheme = themeField?.GetValue(null) is Wpf.Ui.Appearance.ApplicationTheme current
            ? current
            : Wpf.Ui.Appearance.ApplicationTheme.Light;

        try
        {
            foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(theme);

                var style = Find("IrisBorderlessTextBox");
                style.TargetType.ShouldBe(typeof(System.Windows.Controls.TextBox));

                var textBox = new System.Windows.Controls.TextBox
                {
                    Style = style,
                    Text = "Sample",
                    Width = 200,
                    Height = 60,
                };

                var window = new Window
                {
                    Width = 260,
                    Height = 140,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = textBox,
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    textBox.Focus();
                    textBox.UpdateLayout();

                    textBox.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
                    textBox.BorderThickness.ShouldBe(new Thickness(0));
                    var innerBorder = Descendants<System.Windows.Controls.Border>(textBox).FirstOrDefault();
                    innerBorder.ShouldNotBeNull();
                    innerBorder!.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
                    innerBorder.BorderThickness.ShouldBe(new Thickness(0));
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(previousTheme);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(previousTheme);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public void TranslateTo_ReturnsLocalizedString()
    {
        var value = Iris.App.UiText.TranslateTo;
        value.ShouldNotBeNullOrWhiteSpace();
        value.ShouldNotBe("TranslateTo");
    }
    [Fact]
    public Task IrisPillCheckBox_ResolvesAndTogglesInLightAndDarkThemes() => WpfTestRunner.RunAsync(() =>
    {
        try
        {
            foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(theme);

                var checkBox = new System.Windows.Controls.CheckBox
                {
                    Style = Find("IrisPillCheckBox"),
                    Content = "Favorites only",
                };

                var window = new Window
                {
                    Width = 260,
                    Height = 140,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = checkBox,
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();

                    var pillBorder = Descendants<System.Windows.Controls.Border>(checkBox)
                        .FirstOrDefault(b => b.Name == "PillBorder");
                    pillBorder.ShouldNotBeNull();
                    pillBorder!.CornerRadius.TopLeft.ShouldBeGreaterThanOrEqualTo(12d);

                    var starIcon = Descendants<Wpf.Ui.Controls.SymbolIcon>(checkBox)
                        .FirstOrDefault(i => i.Name == "StarIcon");
                    starIcon.ShouldNotBeNull();
                    starIcon!.Symbol.ShouldBe(Wpf.Ui.Controls.SymbolRegular.Star16);
                    starIcon.Filled.ShouldBeFalse();

                    var uncheckedBorderColor = (pillBorder.BorderBrush as SolidColorBrush)?.Color;
                    var uncheckedBackgroundColor = (pillBorder.Background as SolidColorBrush)?.Color;

                    checkBox.IsChecked = true;
                    checkBox.UpdateLayout();

                    starIcon.Filled.ShouldBeTrue();
                    var checkedBorderColor = (pillBorder.BorderBrush as SolidColorBrush)?.Color;
                    var checkedBackgroundColor = (pillBorder.Background as SolidColorBrush)?.Color;

                    (checkedBorderColor != uncheckedBorderColor || checkedBackgroundColor != uncheckedBackgroundColor)
                        .ShouldBeTrue();
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task IrisHistoryListItem_ResolvesAndShowsAccentIndicatorWhenSelectedInLightAndDarkThemes() => WpfTestRunner.RunAsync(() =>
    {
        try
        {
            foreach (var theme in new[] { Wpf.Ui.Appearance.ApplicationTheme.Light, Wpf.Ui.Appearance.ApplicationTheme.Dark })
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(theme);

                var listBox = new System.Windows.Controls.ListBox
                {
                    ItemContainerStyle = Find("IrisHistoryListItem"),
                };
                listBox.Items.Add("Item 1");
                listBox.Items.Add("Item 2");

                var window = new Window
                {
                    Width = 300,
                    Height = 200,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = listBox,
                };

                try
                {
                    window.Show();
                    listBox.SelectedIndex = 0;
                    window.UpdateLayout();

                    var item0 = listBox.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem;
                    var item1 = listBox.ItemContainerGenerator.ContainerFromIndex(1) as System.Windows.Controls.ListBoxItem;
                    item0.ShouldNotBeNull();
                    item1.ShouldNotBeNull();

                    var indicator0 = Descendants<System.Windows.Controls.Border>(item0!)
                        .FirstOrDefault(b => b.Name == "SelectionIndicator");
                    var itemBorder0 = Descendants<System.Windows.Controls.Border>(item0!)
                        .FirstOrDefault(b => b.Name == "ItemBorder");
                    indicator0.ShouldNotBeNull();
                    itemBorder0.ShouldNotBeNull();
                    indicator0!.Visibility.ShouldBe(Visibility.Visible);
                    indicator0.Width.ShouldBe(3d);
                    indicator0.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBeGreaterThan((byte)0);
                    itemBorder0!.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBeGreaterThan((byte)0);

                    var indicator1 = Descendants<System.Windows.Controls.Border>(item1!)
                        .FirstOrDefault(b => b.Name == "SelectionIndicator");
                    var itemBorder1 = Descendants<System.Windows.Controls.Border>(item1!)
                        .FirstOrDefault(b => b.Name == "ItemBorder");
                    indicator1.ShouldNotBeNull();
                    itemBorder1.ShouldNotBeNull();
                    indicator1!.Visibility.ShouldNotBe(Visibility.Visible);
                    itemBorder1!.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
        }

        return Task.CompletedTask;
    });

    private static Style Find(string key) => Application.Current.FindResource(key).ShouldBeOfType<Style>();

    private static Color ColorOf(string key) => Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>().Color;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value)
            {
                yield return value;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
