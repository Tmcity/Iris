using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Iris.App.Tests.TestSupport;
using Shouldly;

namespace Iris.App.Tests.Settings;

public sealed class EditingMenuLifetimeTests
{
    [Fact]
    public Task DefaultEditingMenus_AreIndependentAndKeepStandardCommands() => WpfTestRunner.RunAsync(() =>
    {
        var resources = LoadMenus();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            var first = CreateComboBox();
            var second = CreateComboBox();
            first.ContextMenu.ShouldNotBeNull();
            second.ContextMenu.ShouldNotBeSameAs(first.ContextMenu);
            first.ContextMenu.Items.Cast<MenuItem>().Select(item => item.Command).ShouldBe(
                new ICommand[] { ApplicationCommands.Copy, ApplicationCommands.Cut, ApplicationCommands.Paste });
        }
        finally { Application.Current.Resources.MergedDictionaries.Remove(resources); }
    });

    [Fact]
    public Task ClosedWindow_IsCollectibleWhileAnotherControlKeepsItsMenu() => WpfTestRunner.RunAsync(async () =>
    {
        var resources = LoadMenus();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        var onlooker = new Window { Width = 160, Height = 100, ShowInTaskbar = false, Content = CreateComboBox() };
        try
        {
            var closed = OpenAndCloseOwner();
            onlooker.Show();
            var menu = ((ComboBox)onlooker.Content).ContextMenu;
            menu.ShouldNotBeNull();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            closed.IsAlive.ShouldBeFalse();
            GC.KeepAlive(menu);
        }
        finally
        {
            onlooker.Close();
            Application.Current.Resources.MergedDictionaries.Remove(resources);
        }
    });

    private static ResourceDictionary LoadMenus() => new()
    { Source = new Uri("pack://application:,,,/Iris.App;component/Resources/EditingMenus.xaml", UriKind.Absolute) };

    private static ComboBox CreateComboBox() => new()
    { Style = (Style)Application.Current.FindResource("DefaultComboBoxStyle") };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseOwner()
    {
        var window = new Window { Width = 160, Height = 100, ShowInTaskbar = false, Content = CreateComboBox() };
        window.Show();
        var menu = ((ComboBox)window.Content).ContextMenu;
        menu.ShouldNotBeNull();
        // Reproduce the inherited owner reference observed in the real 0.2.10 heap.
        var ownerProperty = typeof(Window).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(DependencyProperty))
            .Select(field => (DependencyProperty)field.GetValue(null)!)
            .Single(property => property.Name == "IWindowService");
        menu.SetValue(ownerProperty, window);
        Window.GetWindow(menu).ShouldBeSameAs(window);
        var reference = new WeakReference(window);
        window.Close();
        return reference;
    }
}
