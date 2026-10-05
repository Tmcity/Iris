using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Iris.Platform.Windows.Selection;
using Iris.Platform.Windows.Tests.Selection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Selection;

[Collection("AppShutdownerSharedState")]
public sealed class SelectionCoordinationIntegrationTests
{
    [Fact]
    public Task ActualAppDi_DispatcherHandlerCoordinatorAndComposite_ShareOneReader() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.UiaStatus = UiaSelectionStatus.Success;
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { Selection = new SelectionSettings { ReadOrder = [SelectionReadMethod.UIAutomation] } });
        var paths = Substitute.For<IAppPaths>(); paths.DataDirectory.Returns(Path.GetTempPath());
        var ui = Substitute.For<IUiDispatcher>();
        ui.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Func<Task>>()());
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(true);
        var host = AppHostBuilder.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton(settings); services.AddSingleton(s.Desktop); services.AddSingleton(s.Reader);
            services.AddSingleton(s.Uia); services.AddSingleton(s.Keyboard); services.AddSingleton(s.Clipboard);
            services.AddSingleton(ui); services.AddSingleton(presenter);
            services.AddSingleton(Substitute.For<INotificationService>()); services.AddSingleton(Substitute.For<ISecretStore>());
            services.AddSingleton(Substitute.For<IClipboardService>());
        });
        var coordinator = host.Services.GetRequiredService<SelectionReadCoordinator>();
        var dispatcher = (ActionDispatcher)host.Services.GetRequiredService<IActionDispatcher>();
        Exception? primary = null; var errors = new List<Exception>();
        try
        {
            host.Services.GetServices<ISelectionTextReader>().Single().ShouldBeSameAs(coordinator);
            host.Services.GetRequiredService<CompositeSelectionReader>().ShouldBeSameAs(s.Reader);
            host.Services.GetServices<IActionHandler>().OfType<SelectionTranslateHandler>().Count().ShouldBe(1);
            s.Native.Calls.ShouldBeEmpty(); s.Keys.TotalNativeCalls.ShouldBe(0); s.Runner.TotalStartCalls.ShouldBe(0);
            var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
            await s.Time.WaitForDelayAsync(15);
            presenter.ReceivedCalls().ShouldBeEmpty(); // Passive work never creates a Presenter operation.
            var gate = s.NativeGate("Set");
            var action = s.Track(dispatcher.DispatchAsync(IrisAction.SelectionTranslate, new ActionContext(ActionTrigger.Hotkey)));
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            action.IsCompleted.ShouldBeFalse(); s.Runner.TotalStartCalls.ShouldBe(0);
            Should.Throw<IrisException>(() => host.Services.GetRequiredService<CompositeSelectionReader>().ReadAsync(new([], true, 100, []), CancellationToken.None));
            gate.Release.Set(); await action;
            await Should.ThrowAsync<OperationCanceledException>(() => passive);
            s.Runner.TotalStartCalls.ShouldBe(1); s.Copies.ShouldBe(1); s.ClipboardText().ShouldBe("old text");
            await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(request => request.Text == "uia text" && request.AutoTranslate));
            operation.Received(1).Dispose();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var gate in s.Gates) gate.Set();
            dispatcher.StopAcceptance();
            try { await coordinator.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await dispatcher.WaitForInFlightAsync(); await s.Reader.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { host.Dispose(); } catch (Exception ex) { errors.Add(ex); }
        }
        ThrowRetained(primary, errors);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Bootstrapper_DrainsCoordinatorThenCompositeThenPresentersHostAndDispatcher(bool failDestroy) => SelectionCoordinatorTestScope.Run(async (s, coordinator) =>
    {
        s.Seed();
        var calls = new ConcurrentQueue<string>();
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings());
        var notifications = Substitute.For<INotificationService>();
        var ui = Substitute.For<IUiDispatcher>();
        ui.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Func<Task>>()());
        var handler = Substitute.For<IActionHandler>(); handler.Action.Returns(IrisAction.InputTranslate);
        var dispatcher = new ActionDispatcher([handler], ui, notifications, NullLogger<ActionDispatcher>.Instance);
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, dispatcher, NullLogger<TrayIconController>.Instance)
        { SuppressIconCreationForTesting = true };
        var host = Substitute.For<IHost>(); host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { calls.Enqueue("host"); return Task.CompletedTask; });
        var presenter = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        presenter.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(_ => { calls.Enqueue("presenter"); return Task.CompletedTask; });
        var bootstrapper = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), new ThemeService(settings),
            tray, new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host, NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0),
            settingsShutdownCoordinator: presenter, selectionReader: s.Reader, actionDispatcher: dispatcher, selectionReadCoordinator: coordinator);
        var originalShutdown = AppBootstrapper.AppShutdowner;
        AppBootstrapper.AppShutdowner = _ => calls.Enqueue("shutdown");
        var restoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destroyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreRelease = new ManualResetEventSlim(); s.Gates.Add(restoreRelease);
        var destroyRelease = new ManualResetEventSlim(); s.Gates.Add(destroyRelease);
        Exception? primary = null; var errors = new List<Exception>(); Task? stop = null;
        try
        {
            var passive = s.Track(coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
            await s.Time.WaitForDelayAsync(15);
            s.Native.Before = api =>
            {
                if (api == "Set") { restoreEntered.TrySetResult(); restoreRelease.Wait(); calls.Enqueue("restore"); }
                if (api == "Destroy") { destroyEntered.TrySetResult(); destroyRelease.Wait(); calls.Enqueue("destroy"); }
            };
            if (failDestroy) s.Native.Failures["Destroy"] = 1;
            stop = bootstrapper.StopAsync();
            await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.IsCompleted.ShouldBeFalse(); calls.ShouldBeEmpty();
            // Composite is busy but has not been permanently stopped before the coordinator drains.
            Should.Throw<IrisException>(() => s.Reader.ReadAsync(new([], true, 100, []), CancellationToken.None)).Code.ShouldBe(ErrorCode.CaptureFailed);
            (await coordinator.ReadPassiveAsync(SelectionTestScope.Options(), CancellationToken.None)).ShouldBeNull();
            await Should.ThrowAsync<OperationCanceledException>(() => coordinator.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
            await dispatcher.DispatchAsync(IrisAction.InputTranslate, new ActionContext(ActionTrigger.TrayMenu));
            notifications.ReceivedCalls().ShouldBeEmpty();
            await handler.DidNotReceive().ExecuteAsync(Arg.Any<ActionContext>(), Arg.Any<CancellationToken>());
            restoreRelease.Set();
            await destroyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            coordinator.StopAsync().IsCompletedSuccessfully.ShouldBeTrue();
            stop.IsCompleted.ShouldBeFalse(); calls.ShouldBe(new[] { "restore" });
            destroyRelease.Set();
            if (failDestroy)
            {
                await Should.ThrowAsync<IrisException>(() => stop);
                calls.ShouldNotContain("presenter"); calls.ShouldNotContain("host");
                stop = bootstrapper.StopAsync();
            }
            await stop; await Should.ThrowAsync<OperationCanceledException>(() => passive);
            s.Native.WindowAlive.ShouldBeFalse(); s.ClipboardText().ShouldBe("old text");
            var sequence = calls.ToArray();
            Array.IndexOf(sequence, "restore").ShouldBeLessThan(Array.IndexOf(sequence, "destroy"));
            Array.LastIndexOf(sequence, "destroy").ShouldBeLessThan(Array.IndexOf(sequence, "presenter"));
            Array.IndexOf(sequence, "presenter").ShouldBeLessThan(Array.IndexOf(sequence, "host")); sequence[^1].ShouldBe("shutdown");
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            restoreRelease.Set(); destroyRelease.Set(); s.Native.Failures.Clear();
            if (stop != null) try { await stop; } catch (Exception ex) { errors.Add(ex); }
            try { await bootstrapper.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
        ThrowRetained(primary, errors);
    });

    private static void ThrowRetained(Exception? primary, List<Exception> errors)
    {
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
