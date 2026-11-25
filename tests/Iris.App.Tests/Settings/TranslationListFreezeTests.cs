using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Core.Settings;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

public sealed class TranslationListFreezeTests
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
        item.ShouldNotBeNull();
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
    public async Task SavingFreeze_DisablesListAndBlocksAllEntryPoints_AndBreadcrumbBackStillWorks()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            // Dirty valid draft so ExecuteSaveAsync reaches BeforeUpdate
            f.Vm.Profiles[0].DisplayName = "First Provider Edited";
            var gate = f.NewGate();
            f.Settings.BeforeUpdate = () => gate.Pause();
            var saveTask = f.Track(f.Vm.ExecuteSaveAsync());
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await IdleAsync();

                f.Vm.IsSaving.ShouldBeTrue();
                ((UIElement)f.Window.FindName("ProfilesPane")).IsEnabled.ShouldBeFalse();
                ((Button)f.Window.FindName("AddProfileButton")).IsEnabled.ShouldBeFalse();

                var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
                var firstItem = GetListBoxItem(listBox, 0);
                ClickNameArea(FindVisualChild<Border>(firstItem, "TranslationRowNameArea")!);
                ClickElement(FindVisualChild<Button>(firstItem, "TranslationRowOpenDetailsButton")!);
                PressEnterOnItem(listBox, firstItem);
                await IdleAsync();

                f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            }
            finally
            {
                gate.Open();
                await saveTask;
            }

            // Now open detail first, then start another save -> breadcrumb back still works
            var listBox2 = (ListBox)f.Window.FindName("ProfilesListBox");
            ClickNameArea(FindVisualChild<Border>(GetListBoxItem(listBox2, 0), "TranslationRowNameArea")!);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();

            f.Vm.Profiles[0].DisplayName = "First Provider Edited Again";
            var gate2 = f.NewGate();
            f.Settings.BeforeUpdate = () => gate2.Pause();
            var saveTask2 = f.Track(f.Vm.ExecuteSaveAsync());
            try
            {
                await gate2.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await IdleAsync();

                var backBtn = (Button)f.Window.FindName("TranslationBreadcrumbBackButton");
                backBtn.IsEnabled.ShouldBeTrue();
                ClickElement(backBtn);
                await IdleAsync();
                f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            }
            finally
            {
                gate2.Open();
                await saveTask2;
            }
        }, initial: CreateTwoProfileSettings());
    }

    [Fact]
    public async Task ReadOnlyMode_AllowsOpeningAnyRowDetail_WhileDisablingEditsAndReorder()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            f.Vm.CanEdit.ShouldBeFalse();
            f.Vm.IsSaving.ShouldBeFalse();

            ((UIElement)f.Window.FindName("ProfilesPane")).IsEnabled.ShouldBeTrue();
            ((Button)f.Window.FindName("AddProfileButton")).IsEnabled.ShouldBeFalse();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            var secondItem = GetListBoxItem(listBox, 1);

            FindVisualChild<CheckBox>(secondItem)!.IsEnabled.ShouldBeFalse();
            FindVisualChild<Button>(secondItem, "TranslationRowMoreButton")!.IsEnabled.ShouldBeFalse();
            FindVisualChildByTag(secondItem, "DragHandle")!.IsEnabled.ShouldBeFalse();

            // Non-selected row (index 1) can still open detail in read-only mode
            ClickNameArea(FindVisualChild<Border>(secondItem, "TranslationRowNameArea")!);
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-2");
            ((UIElement)f.Window.FindName("DetailsPane")).IsVisible.ShouldBeTrue();
            ((TextBox)f.Window.FindName("DisplayNameTextBox")).IsEnabled.ShouldBeFalse();

            // Breadcrumb back works
            var backBtn = (Button)f.Window.FindName("TranslationBreadcrumbBackButton");
            backBtn.IsEnabled.ShouldBeTrue();
            ClickElement(backBtn);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();

            // Chevron also opens non-selected row
            secondItem = GetListBoxItem(listBox, 1);
            ClickElement(FindVisualChild<Button>(secondItem, "TranslationRowOpenDetailsButton")!);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-2");

            // Return to list, then Enter on ListBoxItem also opens detail in read-only mode
            ClickElement(backBtn);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();

            secondItem = GetListBoxItem(listBox, 1);
            secondItem.Focus();
            PressEnterOnItem(listBox, secondItem);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-2");

            // Return to list, then verify ContextMenuOpening is suppressed and drag-reorder does not reorder
            ClickElement(backBtn);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();

            secondItem = GetListBoxItem(listBox, 1);
            var ctxArgs = (ContextMenuEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ContextMenuEventArgs));
            ctxArgs.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            ctxArgs.Source = secondItem;
            listBox.RaiseEvent(ctxArgs);
            ctxArgs.Handled.ShouldBeTrue();

            var firstIdBefore = f.Vm.Profiles[0].Id;
            var secondIdBefore = f.Vm.Profiles[1].Id;
            var dragHandle = FindVisualChildByTag(secondItem, "DragHandle")!;
            dragHandle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = dragHandle
            });
            dragHandle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseMoveEvent,
                Source = dragHandle
            });
            await IdleAsync();
            f.Vm.Profiles[0].Id.ShouldBe(firstIdBefore);
            f.Vm.Profiles[1].Id.ShouldBe(secondIdBefore);
        }, initial: CreateTwoProfileSettings(), readOnly: true);
    }
}
