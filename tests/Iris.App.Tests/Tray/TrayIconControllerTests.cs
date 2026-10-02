using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Iris.App;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Tray;

public sealed class TrayIconControllerTests
{
    private static (TrayIconController Controller, IActionDispatcher Dispatcher, ContextMenu Menu) CreateSut()
    {
        var appLifetime = Substitute.For<IAppLifetime>();
        var settingsService = Substitute.For<ISettingsService>();
        var actionDispatcher = Substitute.For<IActionDispatcher>();

        var controller = new TrayIconController(
            appLifetime,
            settingsService,
            actionDispatcher,
            NullLogger<TrayIconController>.Instance);

        var createMenuMethod = typeof(TrayIconController).GetMethod("CreateContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        createMenuMethod.ShouldNotBeNull();
        var menu = (ContextMenu)createMenuMethod.Invoke(controller, null)!;

        return (controller, actionDispatcher, menu);
    }

    [Fact]
    public async Task ContextMenu_ItemsAndOrder_AreCorrect()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            var (controller, _, menu) = CreateSut();
            using (controller)
            {
                menu.Items.Count.ShouldBeGreaterThanOrEqualTo(5);

                var item0 = menu.Items[0] as MenuItem;
                item0.ShouldNotBeNull();
                item0.Header.ShouldBe(UiText.TrayMenuInputTranslate);
                controller.InputTranslateMenuItem.ShouldBeSameAs(item0);

                var item1 = menu.Items[1] as MenuItem;
                item1.ShouldNotBeNull();
                item1.Header.ShouldBe(UiText.TrayMenuClipboardTranslate);
                controller.ClipboardTranslateMenuItem.ShouldBeSameAs(item1);

                menu.Items[2].ShouldBeOfType<Separator>();

                var item3 = menu.Items[3] as MenuItem;
                item3.ShouldNotBeNull();
                item3.Header.ShouldBe(UiText.TrayMenuSettings);
                controller.SettingsMenuItem.ShouldBeSameAs(item3);

                menu.Items[4].ShouldBeOfType<Separator>();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InputTranslateMenuItem_Click_DispatchesInputTranslateAction()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (controller, dispatcher, _) = CreateSut();
            using (controller)
            {
                controller.InputTranslateMenuItem.ShouldNotBeNull();
                controller.InputTranslateMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                if (controller.LastInputTranslateTask != null)
                {
                    await controller.LastInputTranslateTask;
                }

                await dispatcher.Received(1).DispatchAsync(
                    IrisAction.InputTranslate,
                    Arg.Is<ActionContext>(ctx => ctx.Trigger == ActionTrigger.TrayMenu),
                    Arg.Any<CancellationToken>());
            }
        });
    }

    [Fact]
    public async Task ClipboardTranslateMenuItem_Click_DispatchesClipboardTranslateAction()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (controller, dispatcher, _) = CreateSut();
            using (controller)
            {
                controller.ClipboardTranslateMenuItem.ShouldNotBeNull();
                controller.ClipboardTranslateMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                if (controller.LastClipboardTranslateTask != null)
                {
                    await controller.LastClipboardTranslateTask;
                }

                await dispatcher.Received(1).DispatchAsync(
                    IrisAction.ClipboardTranslate,
                    Arg.Is<ActionContext>(ctx => ctx.Trigger == ActionTrigger.TrayMenu),
                    Arg.Any<CancellationToken>());
            }
        });
    }

    [Fact]
    public async Task LeftClickCommand_Execute_DispatchesInputTranslateAction()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (controller, dispatcher, _) = CreateSut();
            using (controller)
            {
                controller.LeftClickCommand.ShouldNotBeNull();
                controller.LeftClickCommand.CanExecute(null).ShouldBeTrue();

                controller.LeftClickCommand.Execute(null);

                if (controller.LeftClickCommand is AsyncRelayCommand arc && arc.ExecutionTask != null)
                {
                    await arc.ExecutionTask;
                }

                await dispatcher.Received(1).DispatchAsync(
                    IrisAction.InputTranslate,
                    Arg.Is<ActionContext>(ctx => ctx.Trigger == ActionTrigger.TrayMenu),
                    Arg.Any<CancellationToken>());
            }
        });
    }

    [Fact]
    public async Task MenuClicksAndLeftClick_WhenDispatcherThrows_CatchesAndLogsWithoutCrashing()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var appLifetime = Substitute.For<IAppLifetime>();
            var settingsService = Substitute.For<ISettingsService>();
            var actionDispatcher = Substitute.For<IActionDispatcher>();
            actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => Task.FromException(new InvalidOperationException("Dispatcher failure")));

            using var controller = new TrayIconController(
                appLifetime,
                settingsService,
                actionDispatcher,
                NullLogger<TrayIconController>.Instance);

            var createMenuMethod = typeof(TrayIconController).GetMethod("CreateContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            createMenuMethod.ShouldNotBeNull();
            createMenuMethod.Invoke(controller, null);

            controller.InputTranslateMenuItem.ShouldNotBeNull();
            controller.ClipboardTranslateMenuItem.ShouldNotBeNull();

            // None of these should throw unhandled exception
            controller.InputTranslateMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (controller.LastInputTranslateTask != null)
            {
                await controller.LastInputTranslateTask;
            }

            controller.ClipboardTranslateMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (controller.LastClipboardTranslateTask != null)
            {
                await controller.LastClipboardTranslateTask;
            }

            controller.LeftClickCommand.Execute(null);
            if (controller.LeftClickCommand is AsyncRelayCommand arc && arc.ExecutionTask != null)
            {
                await arc.ExecutionTask;
            }
        });
    }

    [Fact]
    public async Task AfterDispose_LeftClickAndMenuItems_DoNotDispatch()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var (controller, dispatcher, _) = CreateSut();

            var inputItem = controller.InputTranslateMenuItem;
            var clipboardItem = controller.ClipboardTranslateMenuItem;
            var settingsItem = controller.SettingsMenuItem;
            var leftCommand = controller.LeftClickCommand;

            inputItem.ShouldNotBeNull();
            clipboardItem.ShouldNotBeNull();
            settingsItem.ShouldNotBeNull();
            leftCommand.ShouldNotBeNull();

            controller.Dispose();

            leftCommand.CanExecute(null).ShouldBeFalse();

            leftCommand.Execute(null);
            if (leftCommand is AsyncRelayCommand arc && arc.ExecutionTask != null)
            {
                await arc.ExecutionTask;
            }

            inputItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (controller.LastInputTranslateTask != null)
            {
                await controller.LastInputTranslateTask;
            }

            clipboardItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (controller.LastClipboardTranslateTask != null)
            {
                await controller.LastClipboardTranslateTask;
            }

            settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (controller.LastOpenSettingsTask != null)
            {
                await controller.LastOpenSettingsTask;
            }

            await dispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(
                default,
                Arg.Any<ActionContext>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void Construction_DoesNotDispatchActions_OrAccessClipboard()
    {
        var appLifetime = Substitute.For<IAppLifetime>();
        var settingsService = Substitute.For<ISettingsService>();
        var actionDispatcher = Substitute.For<IActionDispatcher>();

        using var controller = new TrayIconController(
            appLifetime,
            settingsService,
            actionDispatcher,
            NullLogger<TrayIconController>.Instance);

        controller.WasInitialized.ShouldBeFalse();
        actionDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default, Arg.Any<ActionContext>(), Arg.Any<CancellationToken>());
    }
}
