using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NSubstitute;
using Shouldly;
using static Iris.App.Tests.History.HistoryTestScope;

namespace Iris.App.Tests.History;

[Collection("AppShutdownerSharedState")]
public sealed class HistoryWindowLayoutTests
{
    [Fact]
    public Task WideAndNarrowLayouts_SwitchCleanlyAt640DipBreakpoint() => Run(async f =>
    {
        f.Desktop.GetMonitors().Returns([new("synthetic", new(0, 0, 1200, 900), new(0, 0, 1200, 900), 1, true)]);
        await f.ShowAsync();
        var window = f.Window;
        window.UpdateLayout();

        var historyScroll = (ScrollViewer)window.FindName("HistoryScroll");
        var detailScroll = (ScrollViewer)window.FindName("DetailScroll");
        var historyList = (ListBox)window.FindName("HistoryList");
        var leftPane = (FrameworkElement)window.FindName("LeftPane");
        var rightPane = (FrameworkElement)window.FindName("RightPane");

        // 800x640 -> wide
        window.IsWideLayout.ShouldBeTrue();
        historyScroll.VerticalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Disabled);
        detailScroll.VerticalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Auto);
        double.IsPositiveInfinity(historyList.MaxHeight).ShouldBeTrue();
        Grid.GetRow(rightPane).ShouldBe(1);
        Grid.GetColumn(rightPane).ShouldBe(1);
        leftPane.ActualWidth.ShouldBe(324, 1.0);
        var leftEdge = leftPane.TransformToAncestor(window).Transform(new Point(leftPane.ActualWidth, 0)).X;
        var rightLeftEdge = rightPane.TransformToAncestor(window).Transform(new Point(0, 0)).X;
        rightLeftEdge.ShouldBeGreaterThanOrEqualTo(leftEdge - 0.1);

        // 640 -> still wide
        window.Width = 640;
        window.UpdateLayout();
        window.IsWideLayout.ShouldBeTrue();

        // 639 -> narrow
        window.Width = 639;
        window.UpdateLayout();
        window.IsWideLayout.ShouldBeFalse();
        historyScroll.VerticalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Auto);
        detailScroll.VerticalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Disabled);
        historyList.MaxHeight.ShouldBe(240);
        Grid.GetRow(rightPane).ShouldBe(2);
        Grid.GetColumn(rightPane).ShouldBe(0);
        var leftBottom = leftPane.TransformToAncestor(window).Transform(new Point(0, leftPane.ActualHeight)).Y;
        var rightTop = rightPane.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
        rightTop.ShouldBeGreaterThanOrEqualTo(leftBottom - 0.1);

        // Back to 800 -> wide again
        window.Width = 800;
        window.UpdateLayout();
        window.IsWideLayout.ShouldBeTrue();
        historyScroll.VerticalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Disabled);
        detailScroll.VerticalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Auto);
        double.IsPositiveInfinity(historyList.MaxHeight).ShouldBeTrue();
        Grid.GetRow(rightPane).ShouldBe(1);
        Grid.GetColumn(rightPane).ShouldBe(1);
    });
    [Theory]
    [InlineData(800, 640, true)]
    [InlineData(640, 640, true)]
    [InlineData(320, 280, false)]
    public Task ConfirmationPanel_VisibleAndPositionedProperlyInBothWideAndNarrowLayouts(int width, int height, bool expectedWide) => Run(async f =>
    {
        f.Desktop.GetMonitors().Returns([new("synthetic", new(0, 0, width, height), new(0, 0, width, height), 1, true)]);
        await f.Repository.Inner.AddAsync(Entry("entry-1"));
        await f.ShowAsync();
        await f.SelectAsync();
        var window = f.Window;
        window.UpdateLayout();
        window.IsWideLayout.ShouldBe(expectedWide);

        var confirmationPanel = (Border)window.FindName("ConfirmationPanel");
        var confirmationText = (TextBlock)window.FindName("ConfirmationMessageTextBlock");
        var keepFavoritesCheckBox = (CheckBox)window.FindName("KeepFavoritesCheckBox");
        var confirmButton = (Button)window.FindName("ConfirmButton");
        var cancelButton = (Button)window.FindName("CancelConfirmationButton");
        var leftPane = (FrameworkElement)window.FindName("LeftPane");

        f.Vm.RequestDelete();
        window.UpdateLayout();

        confirmationPanel.IsVisible.ShouldBeTrue();
        keepFavoritesCheckBox.Visibility.ShouldBe(Visibility.Collapsed);
        confirmButton.IsVisible.ShouldBeTrue();
        cancelButton.IsVisible.ShouldBeTrue();
        Grid.GetColumnSpan(confirmationPanel).ShouldBe(expectedWide ? 2 : 1);
        if (expectedWide)
        {
            confirmationPanel.ActualWidth.ShouldBeGreaterThan(leftPane.ActualWidth);
            confirmationText.ActualWidth.ShouldBeGreaterThan(100);
        }
        else
        {
            confirmationText.ActualWidth.ShouldBeGreaterThan(40);
        }

        confirmButton.TranslatePoint(new Point(confirmButton.ActualWidth, 0), confirmationPanel).X
            .ShouldBeLessThanOrEqualTo(confirmationPanel.ActualWidth + 0.5);
        cancelButton.TranslatePoint(new Point(cancelButton.ActualWidth, 0), confirmationPanel).X
            .ShouldBeLessThanOrEqualTo(confirmationPanel.ActualWidth + 0.5);

        confirmationPanel.BringIntoView();
        window.UpdateLayout();
        confirmationPanel.ActualHeight.ShouldBeGreaterThan(0);
        confirmationPanel.ActualWidth.ShouldBeGreaterThan(0);

        f.Vm.CancelConfirmation();
        window.UpdateLayout();
        confirmationPanel.IsVisible.ShouldBeFalse();

        f.Vm.RequestClear();
        window.UpdateLayout();
        confirmationPanel.IsVisible.ShouldBeTrue();
        keepFavoritesCheckBox.IsVisible.ShouldBeTrue();
        keepFavoritesCheckBox.IsChecked.ShouldBe(true);
    });

    [Fact]
    public Task ConfirmationPanel_EnUsClearAt640Wide_DoesNotCollapseTextOrClipButtons() => Run(async f =>
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            f.Desktop.GetMonitors().Returns([new("synthetic", new(0, 0, 640, 640), new(0, 0, 640, 640), 1, true)]);
            await f.ShowAsync();
            var window = f.Window;
            f.Vm.RequestClear();
            window.UpdateLayout();

            var confirmationPanel = (Border)window.FindName("ConfirmationPanel");
            var confirmationText = (TextBlock)window.FindName("ConfirmationMessageTextBlock");
            var keepFavoritesCheckBox = (CheckBox)window.FindName("KeepFavoritesCheckBox");
            var confirmButton = (Button)window.FindName("ConfirmButton");
            var cancelButton = (Button)window.FindName("CancelConfirmationButton");

            confirmationText.ActualWidth.ShouldBeGreaterThan(100);
            var keepFavPos = keepFavoritesCheckBox.TranslatePoint(new Point(0, 0), confirmationPanel);
            var confirmPos = confirmButton.TranslatePoint(new Point(0, 0), confirmationPanel);
            if (confirmPos.Y > keepFavPos.Y + 4)
            {
                Math.Abs(confirmPos.X - keepFavPos.X).ShouldBeLessThanOrEqualTo(1.5);
            }
            confirmButton.TranslatePoint(new Point(confirmButton.ActualWidth, 0), confirmationPanel).X
                .ShouldBeLessThanOrEqualTo(confirmationPanel.ActualWidth + 0.5);
            cancelButton.TranslatePoint(new Point(cancelButton.ActualWidth, 0), confirmationPanel).X
                .ShouldBeLessThanOrEqualTo(confirmationPanel.ActualWidth + 0.5);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    });

    [Theory]
    [InlineData(320, 280)]
    [InlineData(280, 240)]
    public Task NarrowWorkArea_AllKeyControlsAreScrollableAndReachable(int width, int height) => Run(async f =>
    {
        f.Desktop.GetMonitors().Returns([new("synthetic", new(0, 0, width, height), new(0, 0, width, height), 1, true)]);
        for (int i = 0; i < 51; i++)
            await f.Repository.Inner.AddAsync(Entry($"entry-{i}", favorite: i == 0));

        await f.ShowAsync();
        await f.SelectAsync();
        var window = f.Window;
        window.UpdateLayout();
        window.IsWideLayout.ShouldBeFalse();

        void VerifyReachable(string name)
        {
            var element = (FrameworkElement)window.FindName(name);
            element.ShouldNotBeNull(name);
            element.BringIntoView();
            window.UpdateLayout();
            element.IsVisible.ShouldBeTrue(name);
            element.ActualWidth.ShouldBeGreaterThan(0, name);
            element.ActualHeight.ShouldBeGreaterThan(0, name);
        }

        foreach (var name in new[]
        {
            "SearchTextBox", "FavoritesCheckBox", "RefreshButton", "ClearButton",
            "HistoryList", "PreviousButton", "NextButton",
            "FavoriteButton", "RetranslateButton", "DeleteButton",
            "SourceTextBox", "CopySourceButton", "ResultsList"
        })
        {
            VerifyReachable(name);
        }

        var resultsList = (ItemsControl)window.FindName("ResultsList");
        resultsList.UpdateLayout();
        var firstContainer = (FrameworkElement)resultsList.ItemContainerGenerator.ContainerFromIndex(0);
        firstContainer.ShouldNotBeNull();
        firstContainer.BringIntoView();
        window.UpdateLayout();
        var copyResultBtn = FindVisualChildren<Button>(firstContainer).Single();
        copyResultBtn.BringIntoView();
        window.UpdateLayout();
        copyResultBtn.IsVisible.ShouldBeTrue();
        copyResultBtn.ActualWidth.ShouldBeGreaterThan(0);
        copyResultBtn.ActualHeight.ShouldBeGreaterThan(0);

        f.Vm.RequestClear();
        VerifyReachable("ConfirmationPanel");
        f.Vm.CancelConfirmation();

        var closeButton = (FrameworkElement)window.FindName("CloseButton");
        closeButton.IsVisible.ShouldBeTrue();
        var closeBottomRight = closeButton.TransformToAncestor(window).Transform(new Point(closeButton.ActualWidth, closeButton.ActualHeight));
        closeBottomRight.X.ShouldBeLessThanOrEqualTo(window.ActualWidth + 0.5);
        closeBottomRight.Y.ShouldBeLessThanOrEqualTo(window.ActualHeight + 0.5);
    });

    [Fact]
    public Task CopySourceButton_HiddenWhenUnselectedOrLoadingOrFailed_VisibleWithTooltipWhenDetailLoaded() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("copy source visibility"));
        await f.ShowAsync();
        var window = f.Window;
        var copySource = (Button)window.FindName("CopySourceButton");

        // Before selection
        window.UpdateLayout();
        copySource.IsVisible.ShouldBeFalse();

        // During detail loading
        var gate = f.NewGate();
        f.Repository.GetOverride = async (id, ct) =>
        {
            await gate.Pause();
            return await f.Repository.Inner.GetAsync(id, ct);
        };
        f.Vm.SelectedRow = f.Vm.Rows[0];
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.UpdateLayout();
        copySource.IsVisible.ShouldBeFalse();
        gate.Open();
        await f.Vm.DetailTask;

        // On detail load error
        f.Repository.GetOverride = (_, _) => throw new IOException("synthetic error");
        f.Vm.SelectedRow = null;
        await f.Vm.DetailTask;
        f.Vm.SelectedRow = f.Vm.Rows[0];
        await f.Vm.DetailTask;
        window.UpdateLayout();
        copySource.IsVisible.ShouldBeFalse();

        // After detail loads cleanly
        f.Repository.GetOverride = null;
        f.Vm.SelectedRow = null;
        await f.Vm.DetailTask;
        await f.SelectAsync();
        window.UpdateLayout();
        copySource.IsVisible.ShouldBeTrue();
        copySource.ToolTip.ShouldBe(UiText.HistoryCopySource);
        AutomationProperties.GetName(copySource).ShouldBe(UiText.HistoryCopySource);

        var resultsList = (ItemsControl)window.FindName("ResultsList");
        resultsList.UpdateLayout();
        var firstContainer = (FrameworkElement)resultsList.ItemContainerGenerator.ContainerFromIndex(0);
        var copyResultBtn = FindVisualChildren<Button>(firstContainer).Single();
        copyResultBtn.ToolTip.ShouldBe(UiText.HistoryCopyResult);
        AutomationProperties.GetName(copyResultBtn).ShouldBe(UiText.HistoryCopyResult);
    });

    [Fact]
    public Task HistoryListItem_ShowsFavoriteAndTruncatedPillTagsConditionally() => Run(async f =>
    {
        // (a) favorite + truncated
        await f.Repository.Inner.AddAsync(Entry("both tags", favorite: true));
        // (b) normal (neither)
        await f.Repository.Inner.AddAsync(Entry("no tags", favorite: false) with
        {
            Results = [new("old-a", "Original service A", "old-model-a", "ok")]
        });
        // (c) favorite only
        await f.Repository.Inner.AddAsync(Entry("favorite only", favorite: true) with
        {
            Results = [new("old-a", "Original service A", "old-model-a", "ok")]
        });

        await f.ShowAsync();
        var window = f.Window;
        var list = (ListBox)window.FindName("HistoryList");
        window.UpdateLayout();
        list.UpdateLayout();

        // Rows are ordered newest-first: index 0 = (c) favorite only, index 1 = (b) normal, index 2 = (a) favorite + truncated
        bool HasVisibleTag(int index, string text)
        {
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            return FindVisualChildren<TextBlock>(container)
                .Any(tb => tb.IsVisible && tb.Text == text);
        }

        // (c) favorite only
        HasVisibleTag(0, UiText.HistoryFavorite).ShouldBeTrue();
        HasVisibleTag(0, UiText.HistoryTruncated).ShouldBeFalse();

        // (b) normal (neither)
        HasVisibleTag(1, UiText.HistoryFavorite).ShouldBeFalse();
        HasVisibleTag(1, UiText.HistoryTruncated).ShouldBeFalse();

        // (a) favorite + truncated
        HasVisibleTag(2, UiText.HistoryFavorite).ShouldBeTrue();
        HasVisibleTag(2, UiText.HistoryTruncated).ShouldBeTrue();
    });

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
