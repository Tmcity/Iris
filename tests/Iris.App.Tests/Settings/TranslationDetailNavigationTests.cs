using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Settings;
using Iris.Core.Settings;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

public sealed class TranslationDetailNavigationTests
{
    private static async Task IdleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static AppSettings CreateTwoProfileSettings() => new()
    {
        Translation = new TranslationSettings
        {
            Providers =
            [
                new TranslationProviderProfile
                {
                    Id = "p-1",
                    Kind = TranslationProviderKind.OpenAICompatible,
                    DisplayName = "First Provider",
                    BaseUrl = "https://api.example.com/v1/chat/completions",
                    Model = "gpt-4o-mini",
                    Order = 0
                },
                new TranslationProviderProfile
                {
                    Id = "p-2",
                    Kind = TranslationProviderKind.Anthropic,
                    DisplayName = "Second Provider",
                    BaseUrl = "https://api.anthropic.com/v1/messages",
                    Model = "claude-3-5-haiku-latest",
                    Order = 1
                }
            ]
        }
    };

    private static ListBoxItem GetListBoxItem(ListBox listBox, int index)
    {
        listBox.UpdateLayout();
        var item = (ListBoxItem?)listBox.ItemContainerGenerator.ContainerFromIndex(index);
        item.ShouldNotBeNull($"ListBoxItem at index {index} should be realized.");
        return item;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (name == null || (child is FrameworkElement fe && fe.Name == name)))
                return typed;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static FrameworkElement? FindVisualChildByTag(DependencyObject parent, string tag)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && Equals(fe.Tag, tag))
                return fe;
            var nested = FindVisualChildByTag(child, tag);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static void ClickElement(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));

    private static void ClickNameArea(FrameworkElement nameArea) =>
        nameArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = nameArea
        });

    private static void PressEnterOnItem(ListBox listBox, ListBoxItem item) =>
        listBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(listBox)!, 0, Key.Enter)
        {
            RoutedEvent = UIElement.KeyDownEvent,
            Source = item
        });

    [Fact]
    public async Task DefaultListState_DrillDownViaAllThreeEntries_BreadcrumbBack_AndRowMoreMenu()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            f.Window.TranslationDetailId.ShouldBeNull();

            var profilesPane = (UIElement)f.Window.FindName("ProfilesPane");
            var detailHost = (UIElement)f.Window.FindName("TranslationDetailHost");
            profilesPane.IsVisible.ShouldBeTrue();
            detailHost.IsVisible.ShouldBeFalse();

            // Detail controls remain in namescope even when collapsed
            f.Window.FindName("DisplayNameTextBox").ShouldNotBeNull();
            f.Window.FindName("ApiKeyPasswordBox").ShouldNotBeNull();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            var firstItem = GetListBoxItem(listBox, 0);
            var firstProfile = f.Vm.Profiles[0];

            // 1. Enter via row name+model area
            var nameArea = FindVisualChild<Border>(firstItem, "TranslationRowNameArea");
            nameArea.ShouldNotBeNull();
            ClickNameArea(nameArea);
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe(firstProfile.Id);
            ((TextBlock)f.Window.FindName("TranslationBreadcrumbCurrentText")).Text.ShouldBe(firstProfile.DisplayName);
            ((UIElement)f.Window.FindName("DetailsPane")).IsVisible.ShouldBeTrue();
            ((UIElement)f.Window.FindName("TranslationBreadcrumbBackButton")).IsVisible.ShouldBeTrue();

            // Breadcrumb back
            ClickElement((Button)f.Window.FindName("TranslationBreadcrumbBackButton"));
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            f.Window.TranslationDetailId.ShouldBeNull();

            // 2. Enter via right chevron button
            firstItem = GetListBoxItem(listBox, 0);
            var openBtn = FindVisualChild<Button>(firstItem, "TranslationRowOpenDetailsButton");
            openBtn.ShouldNotBeNull();
            ClickElement(openBtn);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe(firstProfile.Id);

            // Back to list
            ClickElement((Button)f.Window.FindName("TranslationBreadcrumbBackButton"));
            await IdleAsync();

            // 3. Enter via Enter key on ListBoxItem
            firstItem = GetListBoxItem(listBox, 0);
            firstItem.Focus();
            PressEnterOnItem(listBox, firstItem);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe(firstProfile.Id);

            // Back to list
            ClickElement((Button)f.Window.FindName("TranslationBreadcrumbBackButton"));
            await IdleAsync();

            // Clicking drag handle does not enter detail
            firstItem = GetListBoxItem(listBox, 0);
            var dragHandle = FindVisualChildByTag(firstItem, "DragHandle");
            dragHandle.ShouldNotBeNull();
            ClickNameArea(dragHandle);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();

            // Row "..." button opens ContextMenu with MoveUp, MoveDown, Delete
            var moreBtn = FindVisualChild<Button>(firstItem, "TranslationRowMoreButton");
            moreBtn.ShouldNotBeNull();
            ClickElement(moreBtn);
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            moreBtn.ContextMenu.ShouldNotBeNull();
            moreBtn.ContextMenu.Items.Count.ShouldBe(3);

            var moveUpItem = (MenuItem)moreBtn.ContextMenu.Items[0];
            var moveDownItem = (MenuItem)moreBtn.ContextMenu.Items[1];
            var deleteItem = (MenuItem)moreBtn.ContextMenu.Items[2];

            moveUpItem.Header.ShouldBe(UiText.SettingsMoveUp);
            moveDownItem.Header.ShouldBe(UiText.SettingsMoveDown);
            deleteItem.Header.ShouldBe(UiText.SettingsDeleteProfile);

            moveUpItem.IsEnabled.ShouldBeFalse();
            moveDownItem.IsEnabled.ShouldBeTrue();
            deleteItem.IsEnabled.ShouldBeTrue();

            // Move down first profile -> becomes index 1
            moveDownItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, moveDownItem));
            await IdleAsync();
            f.Vm.Profiles[1].Id.ShouldBe(firstProfile.Id);

            // Delete second profile (now at index 1)
            var secondItem = GetListBoxItem(listBox, 1);
            var secondMoreBtn = FindVisualChild<Button>(secondItem, "TranslationRowMoreButton");
            secondMoreBtn.ShouldNotBeNull();
            ClickElement(secondMoreBtn);
            await IdleAsync();
            var secondDelete = (MenuItem)secondMoreBtn.ContextMenu!.Items[2];
            secondDelete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, secondDelete));
            await IdleAsync();
            f.Vm.Profiles.ShouldNotContain(firstProfile);
        }, initial: CreateTwoProfileSettings());
    }

    [Fact]
    public async Task AddProfileFromPreset_AutoEntersDetail()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            var addBtn = (Button)f.Window.FindName("AddProfileButton");
            int countBefore = f.Vm.Profiles.Count;

            ClickElement(addBtn);
            await IdleAsync();

            addBtn.ContextMenu.ShouldNotBeNull();
            addBtn.ContextMenu.Items.Count.ShouldBeGreaterThan(0);

            var firstPreset = (MenuItem)addBtn.ContextMenu.Items[0];
            firstPreset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, firstPreset));
            await IdleAsync();

            f.Vm.Profiles.Count.ShouldBe(countBefore + 1);
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Vm.SelectedProfile.ShouldNotBeNull();
            f.Window.TranslationDetailId.ShouldBe(f.Vm.SelectedProfile.Id);
            ((TextBlock)f.Window.FindName("TranslationBreadcrumbCurrentText")).Text.ShouldBe(f.Vm.SelectedProfile.DisplayName);
        }, initial: CreateTwoProfileSettings());
    }

    [Fact]
    public async Task DetailsPane_AllCheckBoxes_UseIrisSwitchCheckBox()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            var firstItem = GetListBoxItem(listBox, 0);
            ClickNameArea(FindVisualChild<Border>(firstItem, "TranslationRowNameArea")!);
            await IdleAsync();

            var expander = (Expander)f.Window.FindName("AdvancedOptionsExpander");
            expander.IsExpanded = true;
            f.Window.UpdateLayout();

            var switchStyle = f.Window.FindResource("IrisSwitchCheckBox");
            var detailsPane = (DependencyObject)f.Window.FindName("DetailsPane");
            var checkBoxes = new List<CheckBox>();
            CollectLogicalChildren(detailsPane, checkBoxes);

            checkBoxes.Count.ShouldBe(4);
            foreach (var cb in checkBoxes)
            {
                cb.Style.ShouldBeSameAs(switchStyle);
                cb.Content.ShouldBeNull();
            }
        }, initial: CreateTwoProfileSettings());
    }

    private static void CollectLogicalChildren<T>(DependencyObject parent, List<T> results) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T typed)
                results.Add(typed);
            if (child is DependencyObject dep)
                CollectLogicalChildren(dep, results);
        }
    }
    [Fact]
    public async Task DragHandlePressAndRelease_ResetsDragStartedOnHandleFlag() =>
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            var firstItem = GetListBoxItem(listBox, 0);
            var dragHandle = FindVisualChildByTag(firstItem, "DragHandle");
            dragHandle.ShouldNotBeNull();

            var field = typeof(SettingsWindow).GetField("_dragStartedOnHandle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.ShouldNotBeNull();

        listBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = dragHandle
        });
        ((bool)field.GetValue(f.Window)!).ShouldBeTrue();
        listBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            Source = dragHandle
        });
            ((bool)field.GetValue(f.Window)!).ShouldBeFalse();
        }, initial: CreateTwoProfileSettings());
}
