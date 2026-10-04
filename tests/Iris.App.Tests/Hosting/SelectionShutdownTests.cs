using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Selection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Hosting;

[Collection("AppShutdownerSharedState")]
public sealed class SelectionShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Bootstrapper_StopsSelectionBeforePresentersAndHost_AndRetriesFailure(bool failDestroy) => RunScenario(failDestroy, null);

    [Fact]
    public async Task Bootstrapper_TestFailure_ReleasesGateAndFinishesRealShutdown()
    {
        var injected = new InvalidOperationException("test body injection");
        var error = await Should.ThrowAsync<InvalidOperationException>(() => RunScenario(false, injected));
        error.ShouldBeSameAs(injected);
    }

    private static async Task RunScenario(bool failDestroy, Exception? injected)
    {
        var order = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var native = Substitute.For<IClipboardSnapshotNative>();
        native.CreateOwnerWindow().Returns((nint)123);
        native.OpenClipboard(123).Returns(true);
        native.CloseClipboard().Returns(true);
        native.When(n => n.WaitForWork(Arg.Any<WaitHandle>())).Do(ci => ci.Arg<WaitHandle>().WaitOne());
        native.DestroyWindow(123).Returns(_ => { entered.TrySetResult(); release.Wait(); order.Enqueue("selection"); return !failDestroy; });
        var clipboard = new ClipboardSnapshotService(native);
        var keys = new KeyboardSimulator(Substitute.For<INativeKeyboardInputAdapter>());
        var uia = new UiaSelectionReader(Substitute.For<IUiaProcessRunner>(), Substitute.For<ITargetWindowValidator>(), TimeProvider.System);
        var selection = new CompositeSelectionReader(uia, clipboard, keys, Substitute.For<IDesktopContext>(),
            new CopySafetyPolicy(Substitute.For<ICopySafetyNative>()), TimeProvider.System);
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings());
        var ui = Substitute.For<IUiDispatcher>();
        ui.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Func<Task>>()());
        var handler = Substitute.For<IActionHandler>(); handler.Action.Returns(IrisAction.InputTranslate);
        var actionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ExecuteAsync(Arg.Any<ActionContext>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            actionEntered.TrySetResult(); await actionRelease.Task;
        });
        var dispatch = new ActionDispatcher([handler], ui, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, dispatch, NullLogger<TrayIconController>.Instance)
        { SuppressIconCreationForTesting = true };
        var host = Substitute.For<IHost>();
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("host"); return Task.CompletedTask; });
        var capture = Substitute.For<IHotkeyCaptureService>();
        capture.StopAsync().Returns(_ => { order.Enqueue("capture"); return Task.CompletedTask; });
        var presenter = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        var presenterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("presenter"); presenterEntered.TrySetResult(); return Task.CompletedTask; });
        var bootstrapper = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance),
            new ThemeService(settings), tray, new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host,
            NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0), settingsShutdownCoordinator: presenter,
            captureService: capture, selectionReader: selection, actionDispatcher: dispatch);
        var originalShutdown = AppBootstrapper.AppShutdowner;
        AppBootstrapper.AppShutdowner = _ => order.Enqueue("shutdown");
        Exception? primary = null;
        var cleanup = new List<Exception>();
        Task? stop = null;
        Task? action = null;
        try
        {
            await clipboard.ReadVersionAsync();
            action = dispatch.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.TrayMenu));
            await actionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop = bootstrapper.StopAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.IsCompleted.ShouldBeFalse(); order.ShouldContain("capture"); order.ShouldNotContain("presenter"); order.ShouldNotContain("host");
            // Actual ActionDispatcher has already closed acceptance, including tray callers.
            await dispatch.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.TrayMenu));
            handler.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ExecuteAsync").ShouldBe(1);
            if (injected != null) ExceptionDispatchInfo.Capture(injected).Throw();
            release.Set();
            if (failDestroy)
            {
                await Should.ThrowAsync<IrisException>(() => stop);
                order.ShouldNotContain("presenter"); order.ShouldNotContain("host");
                failDestroy = false;
                stop = bootstrapper.StopAsync();
            }
            await presenterEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.IsCompleted.ShouldBeFalse(); order.ShouldNotContain("host");
            actionRelease.TrySetResult(); await action; await stop;
            await dispatch.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.TrayMenu));
            handler.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ExecuteAsync").ShouldBe(1);
            var calls = order.ToArray();
            Array.IndexOf(calls, "capture").ShouldBeLessThan(Array.IndexOf(calls, "selection"));
            Array.LastIndexOf(calls, "selection").ShouldBeLessThan(Array.IndexOf(calls, "presenter"));
            Array.IndexOf(calls, "presenter").ShouldBeLessThan(Array.IndexOf(calls, "host"));
            calls[^1].ShouldBe("shutdown");
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            failDestroy = false; release.Set(); actionRelease.TrySetResult();
            if (action != null) try { await action; } catch (Exception ex) { cleanup.Add(ex); }
            if (stop != null) try { await stop; } catch (Exception ex) { cleanup.Add(ex); }
            try { await bootstrapper.StopAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            try { await selection.DisposeAsync(); await keys.DisposeAsync(); await uia.DisposeAsync(); await clipboard.DisposeAsync(); }
            catch (Exception ex) { cleanup.Add(ex); }
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }
}
