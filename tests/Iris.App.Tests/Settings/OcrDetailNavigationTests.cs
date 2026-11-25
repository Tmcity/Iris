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

public sealed class OcrDetailNavigationTests
{
    private static async Task IdleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static async Task<(Gate Gate, Task Work)> BeginSavingAsync(OcrSettingsUiScope f)
    {
        f.Vm.Ocr.LanguageHint.Value = f.Vm.Ocr.LanguageHint.Value == "en" ? "zh" : "en";
        var gate = f.NewGate();
        f.Settings.BeforeUpdate = () => gate.Pause();
        var work = f.Track(f.Vm.ExecuteSaveAsync());
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Vm.IsSaving.ShouldBeTrue();
        f.Vm.CanEdit.ShouldBeFalse();
        return (gate, work);
    }

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
            if (child is T typed && (name is null || (child is FrameworkElement fe && fe.Name == name)))
                return typed;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static Border GetRowNameArea(ListBoxItem item)
    {
        var border = FindVisualChild<Border>(item, "OcrRowNameArea");
        border.ShouldNotBeNull("OcrRowNameArea should exist in OCR row template.");
        return border;
    }

    private static Button GetRowMoreButton(ListBoxItem item)
    {
        var button = FindVisualChild<Button>(item, "OcrRowMoreButton");
        button.ShouldNotBeNull("OcrRowMoreButton should exist in OCR row template.");
        return button;
    }

    private static Button GetRowOpenDetailsButton(ListBoxItem item)
    {
        var button = FindVisualChild<Button>(item, "OcrRowOpenDetailsButton");
        button.ShouldNotBeNull("OcrRowOpenDetailsButton should exist in OCR row template.");
        return button;
    }

    private static void ClickElement(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void ClickNameArea(Border nameArea) =>
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
    public async Task DefaultListState_DrillDownViaAllThreeEntries_BreadcrumbBack_AndPageSwitchReset() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            f.Window.OcrDetailId.ShouldBeNull();

            var listPanel = (UIElement)f.Window.FindName("OcrListPanel");
            var detailHost = (UIElement)f.Window.FindName("OcrDetailHost");
            listPanel.IsVisible.ShouldBeTrue();
            detailHost.IsVisible.ShouldBeFalse();

            var globalNames = new[]
            {
                "OcrPrimaryCombo", "OcrFallbackCombo", "OcrLayoutCombo",
                "OcrLanguageHintTextBox", "OcrAutoCopyCheck", "OcrSilentNotifyCheck"
            };
            foreach (var name in globalNames)
                ((UIElement)f.Window.FindName(name)).IsVisible.ShouldBeTrue($"{name} should be visible in list state.");

            // Detail controls remain in the same namescope tree even while collapsed.
            f.Window.FindName("OcrNameTextBox").ShouldNotBeNull();
            f.Window.FindName("OcrApiKeyPasswordBox").ShouldNotBeNull();
            var switchStyle = f.Window.FindResource("IrisSwitchCheckBox");
            foreach (var switchName in new[] { "OcrEnabledCheck", "OcrDirectionCheck", "OcrParagraphCheck" })
            {
                var cb = (CheckBox)f.Window.FindName(switchName);
                cb.Style.ShouldBeSameAs(switchStyle, $"{switchName} should use IrisSwitchCheckBox.");
                cb.Content.ShouldBeNull($"{switchName} should not have inline Content.");
            }

            var servicesPane = (DependencyObject)f.Window.FindName("OcrServicesPane");
            foreach (var addName in new[] { "OcrAddBaiduButton", "OcrAddWindowsButton" })
            {
                var cur = (DependencyObject)f.Window.FindName(addName);
                var insideServicesPane = false;
                while (cur is not null)
                {
                    if (ReferenceEquals(cur, servicesPane)) { insideServicesPane = true; break; }
                    cur = VisualTreeHelper.GetParent(cur);
                }
                insideServicesPane.ShouldBeTrue($"{addName} should be inside OcrServicesPane.");
            }

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");
            var firstProfile = f.Vm.OcrProfiles[0];
            var firstItem = GetListBoxItem(listBox, 0);

            // 1. Enter via row name area
            ClickNameArea(GetRowNameArea(firstItem));
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Window.OcrDetailId.ShouldBe(firstProfile.Id);
            ((TextBlock)f.Window.FindName("OcrBreadcrumbCurrentText")).Text.ShouldBe(firstProfile.DisplayName.Value);
            foreach (var name in globalNames)
                ((UIElement)f.Window.FindName(name)).IsVisible.ShouldBeFalse($"{name} should be hidden in detail state.");
            ((UIElement)f.Window.FindName("OcrDetailsPanel")).IsVisible.ShouldBeTrue();
            ((UIElement)f.Window.FindName("OcrTestButton")).IsVisible.ShouldBeTrue();

            // Breadcrumb back
            var backButton = (Button)f.Window.FindName("OcrBreadcrumbBackButton");
            ClickElement(backButton);
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            f.Window.OcrDetailId.ShouldBeNull();
            f.Vm.SelectedOcrProfile.ShouldBeSameAs(firstProfile);

            // 2. Enter via right chevron button
            firstItem = GetListBoxItem(listBox, 0);
            ClickElement(GetRowOpenDetailsButton(firstItem));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Window.OcrDetailId.ShouldBe(firstProfile.Id);

            // Page switch resets detail navigation
            f.Show("general");
            await IdleAsync();
            f.Show("ocr");
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            f.Window.OcrDetailId.ShouldBeNull();

            // 3. Enter via Enter key on ListBoxItem
            firstItem = GetListBoxItem(listBox, 0);
            firstItem.Focus();
            PressEnterOnItem(listBox, firstItem);
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Window.OcrDetailId.ShouldBe(firstProfile.Id);
        });

    [Fact]
    public async Task RowMoreMenu_BuiltInDisabled_CustomDeletesAndReturnsToList_TestCardOutsideFrozenContainers() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            // Add a custom profile first
            f.Vm.AddBaiduOcrCommand.Execute(null);
            await IdleAsync();
            int customIndex = f.Vm.OcrProfiles.Count - 1;
            var customProfile = f.Vm.OcrProfiles[customIndex];
            customProfile.IsBuiltIn.ShouldBeFalse();

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");

            // Built-in row menu has Delete disabled
            var builtInProfile = f.Vm.OcrProfiles.First(p => p.IsBuiltIn);
            int builtInIndex = f.Vm.OcrProfiles.IndexOf(builtInProfile);
            var builtInItem = GetListBoxItem(listBox, builtInIndex);
            var builtInMore = GetRowMoreButton(builtInItem);
            ClickElement(builtInMore);
            builtInMore.ContextMenu.ShouldNotBeNull();
            builtInMore.ContextMenu.Items.Count.ShouldBe(1);
            var builtInDeleteItem = (MenuItem)builtInMore.ContextMenu.Items[0];
            builtInDeleteItem.Header.ShouldBe(UiText.OcrDelete);
            builtInDeleteItem.IsEnabled.ShouldBeFalse();
            builtInMore.ContextMenu.IsOpen = false;
            // Right-click / ContextMenuOpening on custom row populates and opens OcrDelete menu when editable
            var customItemForContext = GetListBoxItem(listBox, customIndex);
            var openArgs = (ContextMenuEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ContextMenuEventArgs));
            openArgs.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            customItemForContext.RaiseEvent(openArgs);
            openArgs.Handled.ShouldBeTrue();
            customItemForContext.ContextMenu.ShouldNotBeNull();
            customItemForContext.ContextMenu.Items.Count.ShouldBe(1);
            ((MenuItem)customItemForContext.ContextMenu.Items[0]).Header.ShouldBe(UiText.OcrDelete);
            ((MenuItem)customItemForContext.ContextMenu.Items[0]).IsEnabled.ShouldBeTrue();
            customItemForContext.ContextMenu.IsOpen = false;
            // Open detail for custom profile, then delete via row menu -> returns to list
            var customItem = GetListBoxItem(listBox, customIndex);
            ClickNameArea(GetRowNameArea(customItem));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeTrue();

            var customMore = GetRowMoreButton(customItem);
            ClickElement(customMore);
            customMore.ContextMenu.ShouldNotBeNull();
            var customDeleteItem = (MenuItem)customMore.ContextMenu.Items[0];
            customDeleteItem.IsEnabled.ShouldBeTrue();
            customDeleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, customDeleteItem));
            await IdleAsync();

            f.Vm.OcrProfiles.ShouldNotContain(customProfile);
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            f.Window.OcrDetailId.ShouldBeNull();

            // Test action card is not inside OcrDetailsPanel or OcrServicesPane
            var testBtn = (DependencyObject)f.Window.FindName("OcrTestButton");
            var cancelBtn = (DependencyObject)f.Window.FindName("OcrCancelTestButton");
            var resultText = (DependencyObject)f.Window.FindName("OcrTestResultText");
            var detailsPanel = f.Window.FindName("OcrDetailsPanel");
            var servicesPane = f.Window.FindName("OcrServicesPane");
            foreach (var el in new[] { testBtn, cancelBtn, resultText })
            {
                DependencyObject? cur = el;
                while (cur is not null)
                {
                    cur.ShouldNotBeSameAs(detailsPanel);
                    cur.ShouldNotBeSameAs(servicesPane);
                    cur = VisualTreeHelper.GetParent(cur);
                }
            }
        });
    [Fact]
    public async Task AddOcrProfile_AutoEntersDetail_AndDoesNotEnterWhenCommandCannotExecute()
    {
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            var addBaidu = (Button)f.Window.FindName("OcrAddBaiduButton");
            var addWindows = (Button)f.Window.FindName("OcrAddWindowsButton");

            // Add Baidu -> auto-enters detail
            ClickElement(addBaidu);
            if (addBaidu.Command?.CanExecute(addBaidu.CommandParameter) == true)
                addBaidu.Command.Execute(addBaidu.CommandParameter);
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Vm.SelectedOcrProfile.ShouldNotBeNull();
            f.Window.OcrDetailId.ShouldBe(f.Vm.SelectedOcrProfile.Id);
            ((TextBlock)f.Window.FindName("OcrBreadcrumbCurrentText")).Text.ShouldBe(f.Vm.SelectedOcrProfile.DisplayName.Value);

            // Back to list, then Add Windows -> auto-enters detail
            ClickElement((Button)f.Window.FindName("OcrBreadcrumbBackButton"));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();

            ClickElement(addWindows);
            if (addWindows.Command?.CanExecute(addWindows.CommandParameter) == true)
                addWindows.Command.Execute(addWindows.CommandParameter);
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Vm.SelectedOcrProfile.ShouldNotBeNull();
            f.Window.OcrDetailId.ShouldBe(f.Vm.SelectedOcrProfile.Id);
            // Back to list
            ClickElement((Button)f.Window.FindName("OcrBreadcrumbBackButton"));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
        });

        // When command cannot execute (read-only / !CanEdit), clicking does not enter detail
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();
            var addBaidu = (Button)f.Window.FindName("OcrAddBaiduButton");
            addBaidu.Command!.CanExecute(addBaidu.CommandParameter).ShouldBeFalse();
            ClickElement(addBaidu);
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
        }, readOnly: true);
    }
    [Fact]
    public async Task SaveAndReload_StayInDetailForNonFirstProfile_AndDeferWhileReadOnly() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            f.Vm.AddBaiduOcrCommand.Execute(null);
            await IdleAsync();
            int secondIndex = f.Vm.OcrProfiles.Count - 1;
            secondIndex.ShouldBeGreaterThan(0);
            var secondProfile = f.Vm.OcrProfiles[secondIndex];
            string targetId = secondProfile.Id;

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");
            var secondItem = GetListBoxItem(listBox, secondIndex);
            ClickNameArea(GetRowNameArea(secondItem));
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Window.OcrDetailId.ShouldBe(targetId);

            // Save -> stays in detail with same profile id
            secondProfile.DisplayName.Value = "Custom OCR Retained";
            await f.Vm.ExecuteSaveAsync();
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Vm.SelectedOcrProfile.ShouldNotBeNull();
            f.Vm.SelectedOcrProfile.Id.ShouldBe(targetId);

            // Reload -> stays in detail with same profile id
            f.Vm.ExecuteReload();
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Vm.SelectedOcrProfile.ShouldNotBeNull();
            f.Vm.SelectedOcrProfile.Id.ShouldBe(targetId);
            // While CanEdit is false during saving, external settings rebuild defers selection restore until CanEdit returns
            var (gate, work) = await BeginSavingAsync(f);
            try
            {
                f.Settings.External(s => s with { General = s.General with { LogLevel = "Debug" } });
                await IdleAsync();
                f.Vm.IsOcrDetailOpen.ShouldBeTrue();
                f.Window.OcrDetailId.ShouldBe(targetId);
            }
            finally
            {
                gate.Open();
                await work;
            }
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            f.Vm.SelectedOcrProfile.ShouldNotBeNull();
            f.Vm.SelectedOcrProfile.Id.ShouldBe(targetId);
            ((ListBox)f.Window.FindName("OcrProfilesListBox")).SelectedItem.ShouldBeSameAs(f.Vm.SelectedOcrProfile);
        });
    [Fact]
    public async Task ReadOnlyMode_OnlySelectedRowCanEnterDetail_OtherRowsDisabled_DetailsReadOnly() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();
            f.Vm.IsReadOnly.ShouldBeTrue();
            var roItem = GetListBoxItem((ListBox)f.Window.FindName("OcrProfilesListBox"), 0);
            roItem.ContextMenu = null;
            var blockedArgs = (ContextMenuEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ContextMenuEventArgs));
            blockedArgs.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            roItem.RaiseEvent(blockedArgs);
            blockedArgs.Handled.ShouldBeTrue();
            roItem.ContextMenu.ShouldBeNull();
            f.Vm.CanEdit.ShouldBeFalse();
            f.Vm.IsSaving.ShouldBeFalse();
            ((UIElement)f.Window.FindName("OcrServicesPane")).IsEnabled.ShouldBeTrue();
            f.Vm.OcrProfiles.Count.ShouldBeGreaterThanOrEqualTo(2);
            var selectedProfile = f.Vm.OcrProfiles[0];
            f.Vm.SelectedOcrProfile = selectedProfile;
            await IdleAsync();

            // 6 global controls, 2 add buttons disabled
            foreach (var name in new[]
                     {
                         "OcrPrimaryCombo", "OcrFallbackCombo", "OcrLayoutCombo",
                         "OcrLanguageHintTextBox", "OcrAutoCopyCheck", "OcrSilentNotifyCheck",
                         "OcrAddBaiduButton", "OcrAddWindowsButton"
                     })
            {
                ((UIElement)f.Window.FindName(name)).IsEnabled.ShouldBeFalse($"{name} should be disabled in read-only mode.");
            }

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");
            var selectedItem = GetListBoxItem(listBox, 0);
            var otherItem = GetListBoxItem(listBox, 1);

            // All row more buttons disabled
            GetRowMoreButton(selectedItem).IsEnabled.ShouldBeFalse();
            GetRowMoreButton(otherItem).IsEnabled.ShouldBeFalse();

            // Selected row entry enabled; non-selected row entry disabled
            GetRowNameArea(selectedItem).IsEnabled.ShouldBeTrue();
            GetRowOpenDetailsButton(selectedItem).IsEnabled.ShouldBeTrue();
            GetRowNameArea(otherItem).IsEnabled.ShouldBeFalse();
            GetRowOpenDetailsButton(otherItem).IsEnabled.ShouldBeFalse();

            // Clicking or pressing Enter on non-selected row does not open detail
            ClickNameArea(GetRowNameArea(otherItem));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            f.Vm.SelectedOcrProfile.ShouldBeSameAs(selectedProfile);
            listBox.SelectedItem.ShouldBeSameAs(f.Vm.SelectedOcrProfile);

            PressEnterOnItem(listBox, otherItem);
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            f.Vm.SelectedOcrProfile.ShouldBeSameAs(selectedProfile);
            listBox.SelectedItem.ShouldBeSameAs(f.Vm.SelectedOcrProfile);

            // Selecting non-selected item directly in ListBox while read-only snaps back so ListBox and VM stay in sync
            listBox.SelectedItem = f.Vm.OcrProfiles[1];
            await IdleAsync();
            f.Vm.SelectedOcrProfile.ShouldBeSameAs(selectedProfile);
            listBox.SelectedItem.ShouldBeSameAs(f.Vm.SelectedOcrProfile);

            // Clicking selected row opens detail, and OcrDetailsPanel is disabled while breadcrumb back is enabled
            ClickNameArea(GetRowNameArea(selectedItem));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            ((UIElement)f.Window.FindName("OcrDetailsPanel")).IsEnabled.ShouldBeFalse();
            var back = (Button)f.Window.FindName("OcrBreadcrumbBackButton");
            back.IsEnabled.ShouldBeTrue();
            ClickElement(back);
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
        }, readOnly: true);

    [Fact]
    public async Task SavingMode_FreezesOcrServicesPane_BreadcrumbRemainsEnabled() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");
            var selectedItem = GetListBoxItem(listBox, 0);
            ClickNameArea(GetRowNameArea(selectedItem));
            await IdleAsync();
            f.Vm.IsOcrDetailOpen.ShouldBeTrue();

            var (gate, work) = await BeginSavingAsync(f);
            try
            {
                ((UIElement)f.Window.FindName("OcrServicesPane")).IsEnabled.ShouldBeFalse();
                var back = (Button)f.Window.FindName("OcrBreadcrumbBackButton");
                back.IsEnabled.ShouldBeTrue();
                ClickElement(back);
                await IdleAsync();
                f.Vm.IsOcrDetailOpen.ShouldBeFalse();

                // In list while saving, cannot enter detail
                selectedItem = GetListBoxItem(listBox, 0);
                ClickNameArea(GetRowNameArea(selectedItem));
                await IdleAsync();
                f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            }
            finally
            {
                gate.Open();
                await work;
            }
        });
    [Fact]
    public async Task CloseOcrDetail_RestoresKeyboardFocusToSelectedListBoxItem() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");
            var firstItem = GetListBoxItem(listBox, 0);
            ClickNameArea(GetRowNameArea(firstItem));
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();

            ClickElement((Button)f.Window.FindName("OcrBreadcrumbBackButton"));
            await IdleAsync();
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            f.Vm.IsOcrDetailOpen.ShouldBeFalse();
            var expectedItem = GetListBoxItem(listBox, 0);
            ReferenceEquals(FocusManager.GetFocusedElement(f.Window), expectedItem).ShouldBeTrue();
        });

    [Fact]
    public async Task AddOcrProfile_AutoEnterMovesFocusToBreadcrumbBackButton() =>
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            var addBaidu = (Button)f.Window.FindName("OcrAddBaiduButton");
            ClickElement(addBaidu);
            if (addBaidu.Command?.CanExecute(addBaidu.CommandParameter) == true)
                addBaidu.Command.Execute(addBaidu.CommandParameter);
            await IdleAsync();
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();
            ReferenceEquals(FocusManager.GetFocusedElement(f.Window), f.Window.FindName("OcrBreadcrumbBackButton")).ShouldBeTrue();
        });
}
