using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.Core.Settings;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

public sealed class SettingsDetailEscapeTests
{
    private static async Task IdleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

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

    private static void SendEscape(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        source.ShouldNotBeNull();
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = UIElement.KeyDownEvent,
            Source = window
        });
    }

    [Fact]
    public async Task Escape_ClosesWindow_WhenTranslationDetailIsOpen()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                Providers =
                [
                    new TranslationProviderProfile
                    {
                        Id = "p-1",
                        Kind = TranslationProviderKind.OpenAICompatible,
                        DisplayName = "Provider 1",
                        BaseUrl = "https://api.example.com/v1/chat/completions",
                        Model = "gpt-4o-mini",
                        Order = 0
                    }
                ]
            }
        };

        await Run(async f =>
        {
            f.Show("translation");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("ProfilesListBox");
            listBox.UpdateLayout();
            var item = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0);
            var nameArea = FindVisualChild<Border>(item, "TranslationRowNameArea");
            nameArea.ShouldNotBeNull();
            nameArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = nameArea
            });
            await IdleAsync();

            f.Vm.IsTranslationDetailOpen.ShouldBeTrue();

            bool closingFired = false;
            bool wasCanceled = true;
            f.Window.Closing += (_, e) =>
            {
                closingFired = true;
                wasCanceled = e.Cancel;
            };

            SendEscape(f.Window);
            await IdleAsync();

            closingFired.ShouldBeTrue();
            wasCanceled.ShouldBeFalse();
        }, initial: settings);
    }

    [Fact]
    public async Task Escape_ClosesWindow_WhenOcrDetailIsOpen()
    {
        await Run(async f =>
        {
            f.Show("ocr");
            await IdleAsync();

            var listBox = (ListBox)f.Window.FindName("OcrProfilesListBox");
            listBox.UpdateLayout();
            var item = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0);
            var nameArea = FindVisualChild<Border>(item, "OcrRowNameArea");
            nameArea.ShouldNotBeNull();
            nameArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = nameArea
            });
            await IdleAsync();

            f.Vm.IsOcrDetailOpen.ShouldBeTrue();

            bool closingFired = false;
            bool wasCanceled = true;
            f.Window.Closing += (_, e) =>
            {
                closingFired = true;
                wasCanceled = e.Cancel;
            };

            SendEscape(f.Window);
            await IdleAsync();

            closingFired.ShouldBeTrue();
            wasCanceled.ShouldBeFalse();
        });
    }
}
