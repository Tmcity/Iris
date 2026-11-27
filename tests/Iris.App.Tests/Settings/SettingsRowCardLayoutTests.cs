using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

// 行卡片版面的守卫（T-041 阶段 2）。任务 5 登记 history、screenshot 两页；
// 任务 6–9 各自把自己的页加进 Page()、ExpectedTexts() 与各条 Theory 的 InlineData。
[Collection("AppShutdownerSharedState")]
public sealed class SettingsRowCardLayoutTests
{
    [Theory]
    [InlineData("history", 2)]
    [InlineData("screenshot", 5)]
    [InlineData("general", 6)]
    [InlineData("selection", 10)]
    [InlineData("network", 6)]
    [InlineData("hotkeys", 8)]
    public Task Page_IsBuiltFromIconRowCards(string pageKey, int expectedRows) => Run(f =>
    {
        f.Show(pageKey);
        f.Window.UpdateLayout();
        var page = Page(f, pageKey);
        page.IsVisible.ShouldBeTrue();
        Descendants<Wpf.Ui.Controls.Card>(page).ShouldBeEmpty();

        var cards = RowCards(f, page);
        cards.Length.ShouldBe(expectedRows);
        foreach (var card in cards)
        {
            var grid = card.Child.ShouldBeOfType<Grid>();
            grid.ColumnDefinitions.Count.ShouldBe(3);
            grid.ColumnDefinitions[0].Width.IsAuto.ShouldBeTrue();
            grid.ColumnDefinitions[1].Width.IsStar.ShouldBeTrue();
            grid.ColumnDefinitions[2].Width.IsAuto.ShouldBeTrue();

            bool hasFullSpanSecondRow = grid.Children.OfType<UIElement>().Any(child =>
                Grid.GetRow(child) == 1 && Grid.GetColumn(child) == 0 && Grid.GetColumnSpan(child) == 3);
            if (pageKey == "hotkeys")
                grid.RowDefinitions.Count.ShouldBe(4);
            else if (hasFullSpanSecondRow)
                grid.RowDefinitions.Count.ShouldBe(2);
            else
                grid.RowDefinitions.Count.ShouldBeLessThanOrEqualTo(1);
            // 图标必须是行 Grid 的直接子元素：文本框、滚动条的模板里也可能有 SymbolIcon，不能按后代找。
            var icon = grid.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().ShouldHaveSingleItem();
            Grid.GetColumn(icon).ShouldBe(0);
            var irisRowIconStyle = f.Window.FindResource("IrisRowIcon");
            (ReferenceEquals(icon.Style, irisRowIconStyle) || ReferenceEquals(icon.Style?.BasedOn, irisRowIconStyle)).ShouldBeTrue();

            grid.Children.OfType<StackPanel>()
                .Where(panel => Grid.GetRow(panel) == 0 && Grid.GetColumn(panel) == 1)
                .ShouldHaveSingleItem();
            string title = RowTitle(f, card).Text;
            title.ShouldNotBeNullOrWhiteSpace();
            // 图标逐行选（协调方裁决 C6）：每一行的图标按行标题登记在 ExpectedIcons() 里。
            ExpectedIcons(pageKey).TryGetValue(title, out var expectedIcon).ShouldBeTrue(title);
            icon.Symbol.ShouldBe(expectedIcon, title);
        }

        // 页的外壳：内边距在第一层面板上（不在 ScrollViewer 上），页标题恰好一个。
        page.ReadLocalValue(Control.PaddingProperty).ShouldBe(DependencyProperty.UnsetValue);
        ((FrameworkElement)page.Content).Margin.ShouldBe((Thickness)f.Window.FindResource("IrisPagePadding"));
        var pageTitleStyle = f.Window.FindResource("IrisPageTitle");
        Descendants<TextBlock>(page).Count(text => ReferenceEquals(text.Style, pageTitleStyle)).ShouldBe(1);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("history", 1)]
    [InlineData("screenshot", 4)]
    [InlineData("general", 3)]
    [InlineData("selection", 3)]
    [InlineData("network", 1)]
    [InlineData("hotkeys", 8)]
    public Task Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow(string pageKey, int expectedSwitches) => Run(f =>
    {
        f.Show(pageKey);
        var boxes = Descendants<CheckBox>(Page(f, pageKey)).ToArray();
        boxes.Length.ShouldBe(expectedSwitches);
        foreach (var box in boxes)
        {
            ReferenceEquals(box.Style, f.Window.FindResource("IrisSwitchCheckBox")).ShouldBeTrue(box.Name);
            box.Content.ShouldBeNull(box.Name);
            Grid.GetColumn(box).ShouldBe(2, box.Name);
            // 没有 Content 之后读屏器只能靠 AutomationProperties.Name；它必须就是本行的标题。
            string name = AutomationProperties.GetName(box);
            name.ShouldNotBeNullOrWhiteSpace(box.Name);
            name.ShouldBe(RowTitle(f, RowCardOf(f, box)).Text, box.Name);
        }
        return Task.CompletedTask;
    });

    // 守卫：改造前后都应该绿。每条既有文字在页内恰好出现一次，一条不丢、一条不重。
    [Theory]
    [InlineData("history")]
    [InlineData("screenshot")]
    [InlineData("general")]
    [InlineData("selection")]
    [InlineData("network")]
    [InlineData("hotkeys")]
    public Task Page_KeepsEveryExistingText(string pageKey) => Run(f =>
    {
        f.Show(pageKey);
        var visible = Descendants<TextBlock>(Page(f, pageKey)).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        foreach (string expected in ExpectedTexts(pageKey))
            visible.Count(text => text == expected).ShouldBe(1, expected);
        return Task.CompletedTask;
    });

    // 680 是导航宽态下内容区最窄的窗口宽度（680 - 176 = 504），600 是窗口最小宽度（S0-2）。
    [Theory]
    [InlineData("history", 680)]
    [InlineData("history", 600)]
    [InlineData("screenshot", 680)]
    [InlineData("screenshot", 600)]
    [InlineData("general", 680)]
    [InlineData("general", 600)]
    [InlineData("selection", 680)]
    [InlineData("selection", 600)]
    [InlineData("network", 680)]
    [InlineData("network", 600)]
    [InlineData("hotkeys", 680)]
    [InlineData("hotkeys", 600)]
    public Task Page_RowsFitTheContentAreaAtNarrowWidths(string pageKey, double width) => Run(f =>
    {
        f.Window.Width = width; f.Window.Height = 440;
        f.Show(pageKey);
        var page = Page(f, pageKey);
        var panel = (FrameworkElement)page.Content;
        var cards = RowCards(f, page);
        cards.ShouldNotBeEmpty();

        page.ScrollableWidth.ShouldBe(0);
        double panelRight = panel.TransformToAncestor(page).Transform(new Point(panel.ActualWidth + panel.Margin.Right, 0)).X;
        panelRight.ShouldBeLessThanOrEqualTo(page.ActualWidth + 0.5);

        foreach (var card in cards)
        {
            double cardRight = card.TransformToAncestor(panel).Transform(new Point(card.ActualWidth, 0)).X;
            cardRight.ShouldBeLessThanOrEqualTo(panel.ActualWidth + 0.5);

            // 第 2 列的控件太宽时，行 Grid 会按自己想要的宽度排、再被卡片裁掉：右边缘越过卡片内缘就是被裁了。
            double inner = card.ActualWidth - card.BorderThickness.Right - card.Padding.Right;
            var grid = (Grid)card.Child;
            foreach (var child in grid.Children.OfType<FrameworkElement>().Where(child => child.IsVisible))
            {
                double right = child.TransformToAncestor(card).Transform(new Point(child.ActualWidth, 0)).X;
                right.ShouldBeLessThanOrEqualTo(inner + 0.5, child.GetType().Name + " " + child.Name);
            }
            // 标题列是 *，被挤到很窄就会一字一行。
            RowTitle(f, card).ActualWidth.ShouldBeGreaterThanOrEqualTo(120);
        }

        var actionCardStyle = f.Window.FindResource("IrisCard");
        foreach (var actionCard in Descendants<Border>(page).Where(border => ReferenceEquals(border.Style, actionCardStyle)))
        {
            double cardRight = actionCard.TransformToAncestor(panel).Transform(new Point(actionCard.ActualWidth, 0)).X;
            cardRight.ShouldBeLessThanOrEqualTo(panel.ActualWidth + 0.5);

            double inner = actionCard.ActualWidth - actionCard.BorderThickness.Right - actionCard.Padding.Right;
            foreach (var button in Descendants<Wpf.Ui.Controls.Button>(actionCard).Where(btn => btn.IsVisible))
            {
                double right = button.TransformToAncestor(actionCard).Transform(new Point(button.ActualWidth, 0)).X;
                right.ShouldBeLessThanOrEqualTo(inner + 0.5, button.Content?.ToString());
            }
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task NamedControls_KeepNameTypeBindingAndEditGate() => Run(f =>
    {
        f.Show("history");
        (string Name, Type Type, DependencyProperty Property, string Path, int Column, double? ExpectedWidth)[] expected =
        [
            ("HistoryEnabledCheckBox", typeof(CheckBox), ToggleButton.IsCheckedProperty, "History.Enabled.Value", 2, null),
            ("HistoryMaxEntriesTextBox", typeof(TextBox), TextBox.TextProperty, "History.MaxEntriesText.Value", 2, 120d),
            ("ScreenshotConfirmCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.ConfirmOnMouseUp.Value", 2, null),
            ("ScreenshotMagnifierCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.ShowMagnifier.Value", 2, null),
            ("ScreenshotHideCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.HideIrisWindows.Value", 2, null),
            ("ScreenshotCopyCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.CopyImageToClipboard.Value", 2, null),
            ("ScreenshotMaskOpacityTextBox", typeof(TextBox), TextBox.TextProperty, "Screenshot.MaskOpacityText.Value", 2, 120d),
            ("UiLanguageCombo", typeof(ComboBox), Selector.SelectedValueProperty, "InterfaceLanguage.Value", 2, null),
            ("CheckForUpdatesCheckBox", typeof(CheckBox), ToggleButton.IsCheckedProperty, "CheckForUpdates.Value", 2, null),
            ("MouseModeComboBox", typeof(ComboBox), Selector.SelectedValueProperty, "Selection.MouseMode", 2, null),
            ("MouseDragTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.MinDragDistanceText", 2, 120d),
            ("MouseHideTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.IconAutoHideText", 2, 120d),
            ("MouseExcludedTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.ExcludedProcessesText", 0, null),
            ("SelectionOrderComboBox", typeof(ComboBox), Selector.SelectedValueProperty, "Selection.ReadOrderChoice", 2, null),
            ("SelectionTimeoutTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.ClipboardTimeoutText", 2, 120d),
            ("SelectionProcessesTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.ClipboardUnsafeProcessesText", 0, null),
            ("NetworkModeCombo", typeof(ComboBox), Selector.SelectedValueProperty, "Network.Mode.Value", 2, null),
            ("NetworkBypassCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Network.BypassLocal.Value", 2, null),
            ("NetworkUrlTextBox", typeof(TextBox), TextBox.TextProperty, "Network.Url.Value", 0, null),
            ("NetworkCredentialsCombo", typeof(ComboBox), Selector.SelectedValueProperty, "Network.Credentials.Value", 2, null),
            ("NetworkUserNameTextBox", typeof(TextBox), TextBox.TextProperty, "Network.UserName.Value", 2, 200d),
        ];
        foreach (var (name, type, property, path, column, expectedWidth) in expected)
        {
            // 前四条是「不许变」的守卫（改造前就绿）：名字、确切类型、绑定路径、仍在 CanEdit 容器里。
            var control = (FrameworkElement)f.Window.FindName(name);
            control.GetType().ShouldBe(type, name);
            var binding = BindingOperations.GetBindingExpression(control, property)
                ?? throw new InvalidOperationException(name + " lost its binding.");
            binding.ParentBinding.Path.Path.ShouldBe(path, name);
            if (property == TextBox.TextProperty)
                binding.ParentBinding.UpdateSourceTrigger.ShouldBe(UpdateSourceTrigger.PropertyChanged, name);
            HasEditGate(control).ShouldBeTrue(name);

            // 后两条是新版面：控件在行卡片指定列，数字文本框宽 120（S0-4）。
            Grid.GetColumn(control).ShouldBe(column, name);
            ReferenceEquals(RowCardOf(f, control).Child, control.Parent).ShouldBeTrue(name);
            if (expectedWidth.HasValue) control.Width.ShouldBe(expectedWidth.Value, name);
        }

        var uiLanguageCombo = (FrameworkElement)f.Window.FindName("UiLanguageCombo");
        var uiLanguageHint = ((FrameworkElement)f.Window.FindName("UiLanguageHint")).ShouldBeOfType<TextBlock>();
        ReferenceEquals(RowCardOf(f, uiLanguageHint), RowCardOf(f, uiLanguageCombo)).ShouldBeTrue();
        Grid.GetColumn((UIElement)uiLanguageHint.Parent).ShouldBe(1);
        BindingOperations.GetBindingExpression(uiLanguageHint, TextBlock.TextProperty)?
            .ParentBinding.Path?.Path.ShouldBe("InterfaceLanguageHint");
        foreach (string multiLineName in new[] { "MouseExcludedTextBox", "SelectionProcessesTextBox" })
        {
            var multiLine = ((FrameworkElement)f.Window.FindName(multiLineName)).ShouldBeOfType<TextBox>();
            Grid.GetRow(multiLine).ShouldBe(1, multiLineName);
            Grid.GetColumnSpan(multiLine).ShouldBe(3, multiLineName);
            multiLine.Height.ShouldBe(130, multiLineName);
            multiLine.AcceptsReturn.ShouldBeTrue(multiLineName);
        }

        var selectionPage = Page(f, "selection");
        f.Show("selection");
        var selectionPanel = (StackPanel)selectionPage.Content;
        var selectionError = ((FrameworkElement)f.Window.FindName("SelectionErrorTextBlock")).ShouldBeOfType<TextBlock>();
        ReferenceEquals(selectionError.Parent, selectionPanel).ShouldBeTrue();
        BindingOperations.GetBindingExpression(selectionError, TextBlock.TextProperty)?
            .ParentBinding.Path?.Path.ShouldBe("Selection.ErrorMessage");
        var firstSelectionCard = RowCards(f, selectionPage).First();
        int errorIndex = selectionPanel.Children.IndexOf(selectionError);
        int firstCardIndex = selectionPanel.Children.IndexOf(firstSelectionCard);
        errorIndex.ShouldBeGreaterThanOrEqualTo(0);
        errorIndex.ShouldBeLessThan(firstCardIndex);
        var passwordBox = ((FrameworkElement)f.Window.FindName("NetworkPasswordBox")).ShouldBeOfType<PasswordBox>();
        passwordBox.GetType().ShouldBe(typeof(PasswordBox));
        passwordBox.Width.ShouldBe(200d);
        Grid.GetColumn(passwordBox).ShouldBe(2);
        ReferenceEquals(RowCardOf(f, passwordBox).Child, passwordBox.Parent).ShouldBeTrue();
        HasEditGate(passwordBox).ShouldBeTrue();
        bool hasIsReplacingGate = false;
        for (DependencyObject? current = passwordBox; current is FrameworkElement parent; current = parent.Parent)
        {
            if (BindingOperations.GetBindingExpression(parent, UIElement.IsEnabledProperty)?.ParentBinding.Path?.Path == "Network.IsReplacing")
            {
                hasIsReplacingGate = true;
                break;
            }
        }
        hasIsReplacingGate.ShouldBeTrue();
        return Task.CompletedTask;
    });

    [Fact]
    public Task GeneralPage_UpdateActionCardKeepsVersionStatusProgressAndFourButtons() => Run(f =>
    {
        f.Show("general");
        var page = Page(f, "general");
        var actionCardStyle = f.Window.FindResource("IrisCard");
        var actionCard = Descendants<Border>(page)
            .Where(border => ReferenceEquals(border.Style, actionCardStyle))
            .ShouldHaveSingleItem();
        Descendants<Wpf.Ui.Controls.SymbolIcon>(actionCard).ShouldBeEmpty();

        var version = Descendants<TextBlock>(actionCard).Single(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "CurrentVersion");
        AutomationProperties.GetName(version).ShouldBe(UiText.UpdateTitle);
        _ = Descendants<TextBlock>(actionCard).Single(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "UpdateStatus");
        _ = Descendants<TextBlock>(actionCard).Single(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "UpdateDetail");

        var progress = Descendants<ProgressBar>(actionCard).ShouldHaveSingleItem();
        BindingOperations.GetBindingExpression(progress, RangeBase.ValueProperty)?
            .ParentBinding.Path?.Path.ShouldBe("UpdateProgress");

        var wrapPanel = Descendants<WrapPanel>(actionCard).ShouldHaveSingleItem();
        var buttons = wrapPanel.Children.OfType<Wpf.Ui.Controls.Button>().ToArray();
        buttons.Select(btn => btn.Content?.ToString()).ShouldBe([
            UiText.UpdateCheck,
            UiText.UpdateDownload,
            UiText.UpdateCancel,
            UiText.UpdateInstall,
        ]);
        foreach (var btn in buttons)
            HasEditGate(btn).ShouldBeFalse(btn.Content?.ToString());

        return Task.CompletedTask;
    });
    [Fact]
    public Task NetworkPage_TestActionCardStaysOutsideEditGateAndKeepsTestControls() => Run(f =>
    {
        f.Show("network");
        var page = Page(f, "network");
        var descriptionStyle = f.Window.FindResource("IrisRowDescription");
        var description = Descendants<TextBlock>(page).Single(text => text.Text == UiText.NetworkDescription);
        ReferenceEquals(description.Style, descriptionStyle).ShouldBeTrue();
        HasEditGate(description).ShouldBeFalse();

        var validationText = ((FrameworkElement)f.Window.FindName("NetworkValidationText")).ShouldBeOfType<TextBlock>();
        ReferenceEquals(validationText.Style, f.Window.FindResource("IrisRowError")).ShouldBeTrue();
        validationText.Visibility.ShouldBe(Visibility.Collapsed);
        HasEditGate(validationText).ShouldBeFalse();

        var actionCardStyle = f.Window.FindResource("IrisCard");
        var actionCard = Descendants<Border>(page)
            .Where(border => ReferenceEquals(border.Style, actionCardStyle))
            .ShouldHaveSingleItem();
        Descendants<Wpf.Ui.Controls.SymbolIcon>(actionCard).Any(icon => ReferenceEquals(icon.Style, f.Window.FindResource("IrisRowIcon"))).ShouldBeFalse();

        var endpointCombo = ((FrameworkElement)f.Window.FindName("NetworkEndpointCombo")).ShouldBeOfType<ComboBox>();
        endpointCombo.IsDescendantOf(actionCard).ShouldBeTrue();
        BindingOperations.GetBindingExpression(endpointCombo, UIElement.IsEnabledProperty)?
            .ParentBinding.Path?.Path.ShouldBe("CanEdit");

        foreach (string name in new[] { "NetworkTestButton", "NetworkCancelButton", "NetworkStatusText", "NetworkResultText" })
        {
            var element = (FrameworkElement)f.Window.FindName(name);
            element.IsDescendantOf(actionCard).ShouldBeTrue(name);
            HasEditGate(element).ShouldBeFalse(name);
        }

        BindingOperations.GetBindingExpression((FrameworkElement)f.Window.FindName("NetworkStatusText"), TextBlock.TextProperty)?.ParentBinding.Path?.Path.ShouldBe("NetworkTestStatus");
        BindingOperations.GetBindingExpression((FrameworkElement)f.Window.FindName("NetworkResultText"), TextBlock.TextProperty)?.ParentBinding.Path?.Path.ShouldBe("NetworkResultText");
        Descendants<TextBlock>(actionCard).Any(t => BindingOperations.GetBindingExpression(t, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "NetworkEndpointHint").ShouldBeTrue();
        return Task.CompletedTask;
    });
    [Theory]
    [InlineData(680)]
    [InlineData(600)]
    public Task HotkeysPage_RecorderBoxesKeepMinWidth360AndDistinctActionIcons(double width) => Run(f =>
    {
        f.Window.Width = width;
        f.Window.Height = 440;
        f.Show("hotkeys");
        f.Window.UpdateLayout();

        var page = Page(f, "hotkeys");
        var recorders = Descendants<Iris.App.Settings.HotkeyRecorderBox>(page).ToArray();
        recorders.Length.ShouldBe(8);
        foreach (var recorder in recorders)
        {
            recorder.MinWidth.ShouldBe(360d);
            recorder.MaxWidth.ShouldBe(double.PositiveInfinity);
            recorder.ActualWidth.ShouldBeGreaterThanOrEqualTo(360d);

            var card = RowCardOf(f, recorder);
            double inner = card.ActualWidth - card.BorderThickness.Right - card.Padding.Right;
            double right = recorder.TransformToAncestor(card).Transform(new Point(recorder.ActualWidth, 0)).X;
            right.ShouldBeLessThanOrEqualTo(inner + 0.5);
        }

        var cards = RowCards(f, page);
        cards.Length.ShouldBe(8);
        var symbols = cards
            .Select(card => ((Grid)card.Child).Children.OfType<Wpf.Ui.Controls.SymbolIcon>().Single().Symbol)
            .Distinct()
            .ToArray();
        symbols.Length.ShouldBe(8);

        var panel = (StackPanel)page.Content;
        BindingOperations.GetBindingExpression(panel, UIElement.IsEnabledProperty)?
            .ParentBinding.Path?.Path.ShouldBe("CanAccessHotkeysTab");
        var descriptionStyle = f.Window.FindResource("IrisRowDescription");
        panel.Children.OfType<TextBlock>().Any(text =>
            text.Text == UiText.SettingsHotkeysDescription && ReferenceEquals(text.Style, descriptionStyle)).ShouldBeTrue();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("history", "History.ErrorMessage", "HistoryMaxEntriesTextBox")]
    [InlineData("screenshot", "Screenshot.ErrorMessage", "ScreenshotMaskOpacityTextBox")]
    public Task ErrorText_SitsInItsRowAndCollapsesWhileEmpty(string pageKey, string errorPath, string inputName) => Run(async f =>
    {
        f.Show(pageKey);
        var error = Descendants<TextBlock>(Page(f, pageKey)).Single(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == errorPath);
        // 行内错误文字统一用主题层的 IrisRowError（协调方裁决 C5），不在行里写内联样式。
        ReferenceEquals(error.Style, f.Window.FindResource("IrisRowError")).ShouldBeTrue();
        // 没有错误时折叠，不给行卡片多撑出一行。
        error.Visibility.ShouldBe(Visibility.Collapsed);

        // 错误文字与它说的那个输入框在同一张行卡片里，位于第 1 列的文字栈中。
        var input = (TextBox)f.Window.FindName(inputName);
        ReferenceEquals(RowCardOf(f, error), RowCardOf(f, input)).ShouldBeTrue();
        Grid.GetColumn((UIElement)error.Parent).ShouldBe(1);

        if (pageKey == "history") f.Vm.History.MaxEntriesText.Value = "abc";
        else f.Vm.Screenshot.MaskOpacityText.Value = "NaN";
        await f.Vm.ExecuteSaveAsync();
        f.Window.UpdateLayout();

        error.Text.ShouldNotBeNullOrWhiteSpace();
        error.Visibility.ShouldBe(Visibility.Visible);
        error.IsVisible.ShouldBeTrue();
    });

    private static ScrollViewer Page(OcrSettingsUiScope f, string pageKey) => (ScrollViewer)f.Window.FindName(pageKey switch
    {
        "history" => "HistoryPageScroll",
        "screenshot" => "ScreenshotPageScroll",
        "general" => "GeneralPageScroll",
        "selection" => "SelectionScrollViewer",
        "network" => "NetworkPageScroll",
        "hotkeys" => "HotkeysPageScroll",
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, null),
    });

    private static string[] ExpectedTexts(string pageKey) => pageKey switch
    {
        "history" =>
        [
            UiText.HistoryTitle, UiText.HistoryEnabled, UiText.HistoryEnabledHelp, UiText.HistoryMaxLabel, UiText.HistoryMaxHelp,
        ],
        "screenshot" =>
        [
            UiText.ScreenshotPageTitle, UiText.ScreenshotSettingsDescription, UiText.ScreenshotConfirmOnMouseUp,
            UiText.ScreenshotShowMagnifier, UiText.ScreenshotHideWindows, UiText.ScreenshotCopyImage, UiText.ScreenshotOpacity,
        ],
        "general" =>
        [
            UiText.SettingsNavGeneral,
            UiText.LanguageTitle,
            UiText.UpdateTitle,
            UiText.UpdatePreference,
            UiText.SettingsThemeGroup,
            UiText.SettingsThemeDescription,
            UiText.SettingsStartupGroup,
            UiText.SettingsLaunchAtStartup,
            UiText.SettingsShowStartupNotification,
            UiText.SettingsLogGroup,
            UiText.SettingsLogDescription,
        ],
        "selection" =>
        [
            UiText.SettingsNavSelection,
            UiText.MouseModeLabel,
            UiText.MouseExplanation,
            UiText.MouseBeamLabel,
            UiText.MouseDragLabel,
            UiText.MouseHideLabel,
            UiText.MouseExcludedLabel,
            UiText.SelectionOrderLabel,
            UiText.SelectionSafetyDescription,
            UiText.SelectionTimeoutLabel,
            UiText.SelectionTimeoutDescription,
            UiText.SelectionRestoreLabel,
            UiText.SelectionRestoreDescription,
            UiText.SelectionEmptyLabel,
            UiText.SelectionEmptyDescription,
            UiText.SelectionProcessesLabel,
            UiText.SelectionProcessesDescription,
        ],
        "network" =>
        [
            UiText.NetworkTitle,
            UiText.NetworkDescription,
            UiText.NetworkModeLabel,
            UiText.NetworkBypass,
            UiText.NetworkBypassDescription,
            UiText.NetworkUrlLabel,
            UiText.NetworkUrlHint,
            UiText.NetworkCredentialLabel,
            UiText.NetworkCredentialHint,
            UiText.NetworkUserName,
            UiText.NetworkPassword,
            UiText.NetworkTargetLabel,
            UiText.NetworkTargetHint,
        ],
        "hotkeys" =>
        [
            UiText.SettingsNavHotkeys,
            UiText.SettingsHotkeysDescription,
            UiText.TrayMenuInputTranslate,
            UiText.TrayMenuClipboardTranslate,
            UiText.SettingsTitle,
            UiText.SettingsNavSelection,
            UiText.SelectionHotkeyDescription,
            UiText.MouseToggle,
            UiText.MouseToggleDescription,
            UiText.TrayMenuScreenshotTranslate,
            UiText.ScreenshotTranslateHotkeyDescription,
            UiText.TrayMenuScreenshotOcr,
            UiText.ScreenshotOcrHotkeyDescription,
            UiText.TrayMenuSilentScreenshotOcr,
            UiText.SilentScreenshotOcrHotkeyDescription,
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, null),
    };

    // 行标题 → 该行的图标。必须是属性取值时现算（不能做成 static readonly）：UiText 的值随界面语言变。
    private static Dictionary<string, Wpf.Ui.Controls.SymbolRegular> ExpectedIcons(string pageKey) => pageKey switch
    {
        "history" => new()
        {
            [UiText.HistoryEnabled] = Wpf.Ui.Controls.SymbolRegular.TextBulletListAdd20,
            [UiText.HistoryMaxLabel] = Wpf.Ui.Controls.SymbolRegular.NumberSymbol20,
        },
        "screenshot" => new()
        {
            [UiText.ScreenshotConfirmOnMouseUp] = Wpf.Ui.Controls.SymbolRegular.CursorClick20,
            [UiText.ScreenshotShowMagnifier] = Wpf.Ui.Controls.SymbolRegular.ZoomIn20,
            [UiText.ScreenshotHideWindows] = Wpf.Ui.Controls.SymbolRegular.EyeOff20,
            [UiText.ScreenshotCopyImage] = Wpf.Ui.Controls.SymbolRegular.ClipboardImage20,
            [UiText.ScreenshotOpacity] = Wpf.Ui.Controls.SymbolRegular.Options20,
        },
        "general" => new()
        {
            [UiText.LanguageTitle] = Wpf.Ui.Controls.SymbolRegular.LocalLanguage20,
            [UiText.UpdatePreference] = Wpf.Ui.Controls.SymbolRegular.ArrowSync20,
            [UiText.SettingsThemeGroup] = Wpf.Ui.Controls.SymbolRegular.Color20,
            [UiText.SettingsLaunchAtStartup] = Wpf.Ui.Controls.SymbolRegular.Power20,
            [UiText.SettingsShowStartupNotification] = Wpf.Ui.Controls.SymbolRegular.Power20,
            [UiText.SettingsLogGroup] = Wpf.Ui.Controls.SymbolRegular.DocumentText20,
        },
        "selection" => new()
        {
            [UiText.MouseModeLabel] = Wpf.Ui.Controls.SymbolRegular.CursorClick20,
            [UiText.MouseBeamLabel] = Wpf.Ui.Controls.SymbolRegular.CursorHover20,
            [UiText.MouseDragLabel] = Wpf.Ui.Controls.SymbolRegular.Cursor20,
            [UiText.MouseHideLabel] = Wpf.Ui.Controls.SymbolRegular.Timer20,
            [UiText.MouseExcludedLabel] = Wpf.Ui.Controls.SymbolRegular.DismissSquare20,
            [UiText.SelectionOrderLabel] = Wpf.Ui.Controls.SymbolRegular.Options20,
            [UiText.SelectionTimeoutLabel] = Wpf.Ui.Controls.SymbolRegular.Options20,
            [UiText.SelectionRestoreLabel] = Wpf.Ui.Controls.SymbolRegular.ClipboardPaste20,
            [UiText.SelectionEmptyLabel] = Wpf.Ui.Controls.SymbolRegular.Clipboard20,
            [UiText.SelectionProcessesLabel] = Wpf.Ui.Controls.SymbolRegular.ShieldKeyhole20,
        },
        "network" => new()
        {
            [UiText.NetworkModeLabel] = Wpf.Ui.Controls.SymbolRegular.Globe20,
            [UiText.NetworkBypass] = Wpf.Ui.Controls.SymbolRegular.GlobeProhibited20,
            [UiText.NetworkUrlLabel] = Wpf.Ui.Controls.SymbolRegular.Server20,
            [UiText.NetworkCredentialLabel] = Wpf.Ui.Controls.SymbolRegular.Key20,
            [UiText.NetworkUserName] = Wpf.Ui.Controls.SymbolRegular.Key20,
            [UiText.NetworkPassword] = Wpf.Ui.Controls.SymbolRegular.Key20,
        },
        "hotkeys" => new()
        {
            [UiText.TrayMenuInputTranslate] = Wpf.Ui.Controls.SymbolRegular.Translate20,
            [UiText.TrayMenuClipboardTranslate] = Wpf.Ui.Controls.SymbolRegular.ClipboardTextLtr20,
            [UiText.SettingsTitle] = Wpf.Ui.Controls.SymbolRegular.Settings20,
            [UiText.SettingsNavSelection] = Wpf.Ui.Controls.SymbolRegular.CursorClick20,
            [UiText.MouseToggle] = Wpf.Ui.Controls.SymbolRegular.CursorHover20,
            [UiText.TrayMenuScreenshotTranslate] = Wpf.Ui.Controls.SymbolRegular.Screenshot20,
            [UiText.TrayMenuScreenshotOcr] = Wpf.Ui.Controls.SymbolRegular.ScanText16,
            [UiText.TrayMenuSilentScreenshotOcr] = Wpf.Ui.Controls.SymbolRegular.Camera16,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, null),
    };

    private static Border[] RowCards(OcrSettingsUiScope f, DependencyObject root)
    {
        var style = f.Window.FindResource("IrisRowCard");
        return Descendants<Border>(root).Where(border => ReferenceEquals(border.Style, style)).ToArray();
    }

    private static Border RowCardOf(OcrSettingsUiScope f, FrameworkElement element)
    {
        var style = f.Window.FindResource("IrisRowCard");
        for (DependencyObject? current = element.Parent; current is FrameworkElement parent; current = parent.Parent)
            if (parent is Border border && ReferenceEquals(border.Style, style)) return border;
        throw new InvalidOperationException($"{element.GetType().Name} '{element.Name}' is not inside an IrisRowCard.");
    }

    private static TextBlock RowTitle(OcrSettingsUiScope f, Border card)
    {
        var style = f.Window.FindResource("IrisRowTitle");
        return Descendants<TextBlock>(card).First(text => ReferenceEquals(text.Style, style));
    }

    private static bool HasEditGate(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is FrameworkElement parent; current = parent.Parent)
            if (BindingOperations.GetBindingExpression(parent, UIElement.IsEnabledProperty)?.ParentBinding.Path?.Path == "CanEdit") return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
