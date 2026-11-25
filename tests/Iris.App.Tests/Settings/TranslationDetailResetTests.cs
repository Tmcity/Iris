using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Core.Settings;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

public sealed class TranslationDetailResetTests
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

    private static void OpenRowDetail(ListBox listBox, int index)
    {
        listBox.UpdateLayout();
        var item = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(index);
        var nameArea = FindVisualChild<Border>(item, "TranslationRowNameArea");
        nameArea.ShouldNotBeNull();
        nameArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = nameArea
        });
    }

    [Fact]
    public async Task PageSwitch_ResetsDetailToList()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            OpenRowDetail(listBox, 0);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-1");

            // Re-navigating to the same page ("translation") keeps detail open
            f.Show("translation");
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-1");

            f.Show("general");
            await IdleAsync();
            f.Show("translation");
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            f.Window.TranslationDetailId.ShouldBeNull();
        }, initial: CreateTwoProfileSettings());
    }

    [Fact]
    public async Task ExternalSettingsChange_RemovingCurrentProfile_ReturnsToList_KeepingCurrentProfile_StaysInDetail()
    {
        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            OpenRowDetail(listBox, 1);
            await IdleAsync();
            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-2");

            // External update keeping p-2 -> stays in detail
            f.Settings.External(s => s with
            {
                Translation = s.Translation with
                {
                    Providers =
                    [
                        s.Translation.Providers[0],
                        s.Translation.Providers[1] with { DisplayName = "Second Updated" }
                    ]
                }
            });
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();
            f.Window.TranslationDetailId.ShouldBe("p-2");

            // External update removing p-2 -> returns to list
            f.Settings.External(s => s with
            {
                Translation = s.Translation with
                {
                    Providers = [s.Translation.Providers[0]]
                }
            });
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeFalse();
            f.Window.TranslationDetailId.ShouldBeNull();
        }, initial: CreateTwoProfileSettings());
    }
}
