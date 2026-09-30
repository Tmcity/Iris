using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Tray;

public sealed class TrayIconControllerSettingsTests
{
    private static async Task RunInStaAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await tcs.Task;
    }

    [Fact]
    public async Task SettingsMenuItem_Click_DispatchesOpenSettingsAction()
    {
        await RunInStaAsync(() =>
        {
            var appLifetime = Substitute.For<IAppLifetime>();
            var settingsService = Substitute.For<ISettingsService>();
            var actionDispatcher = Substitute.For<IActionDispatcher>();

            using var controller = new TrayIconController(
                appLifetime,
                settingsService,
                actionDispatcher,
                NullLogger<TrayIconController>.Instance);

            // 通过私有方法 CreateContextMenu 构造菜单项
            var createMenuMethod = typeof(TrayIconController).GetMethod("CreateContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            createMenuMethod.ShouldNotBeNull();
            var menu = (ContextMenu)createMenuMethod.Invoke(controller, null)!;

            menu.ShouldNotBeNull();
            controller.SettingsMenuItem.ShouldNotBeNull();
            controller.SettingsMenuItem.IsEnabled.ShouldBeTrue();

            // 触发点击事件
            controller.SettingsMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            actionDispatcher.Received(1).DispatchAsync(
                IrisAction.OpenSettings,
                Arg.Is<ActionContext>(ctx => ctx.Trigger == ActionTrigger.TrayMenu));

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SettingsMenuItem_Click_WhenDispatcherThrows_CatchesAndLogsWithoutCrashing()
    {
        await RunInStaAsync(() =>
        {
            var appLifetime = Substitute.For<IAppLifetime>();
            var settingsService = Substitute.For<ISettingsService>();
            var actionDispatcher = Substitute.For<IActionDispatcher>();
            actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>())
                .Returns<Task>(_ => Task.FromException(new InvalidOperationException("Simulated dispatcher failure")));

            using var controller = new TrayIconController(
                appLifetime,
                settingsService,
                actionDispatcher,
                NullLogger<TrayIconController>.Instance);

            var createMenuMethod = typeof(TrayIconController).GetMethod("CreateContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            createMenuMethod.ShouldNotBeNull();
            createMenuMethod.Invoke(controller, null);

            controller.SettingsMenuItem.ShouldNotBeNull();

            // 不抛出未捕获异常
            Should.NotThrow(() =>
            {
                controller.SettingsMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            });

            return Task.CompletedTask;
        });
    }
}
