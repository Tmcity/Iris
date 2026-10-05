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
using Iris.Platform.Windows.Capture;
using Iris.Platform.Windows.Tests.Capture;
using Iris.Platform.Windows.Tests.Selection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Hosting;

[Collection("AppShutdownerSharedState")]
public sealed class ScreenCaptureShutdownTests
{
    [Fact]
    public async Task ActualHostResolvesSameLazyCapturerWithoutNativeOrWorker()
    {
        var paths = Substitute.For<IAppPaths>(); paths.DataDirectory.Returns(Path.GetTempPath());
        using var native = new FakeGdiCaptureApi(); var desktop = new CaptureDesktop(); var threads = new CaptureThreads();
        using var host = AppHostBuilder.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton<IGdiCaptureApi>(native); services.AddSingleton<ICaptureThreadFactory>(threads); services.AddSingleton<IDesktopContext>(desktop);
        });
        var service = host.Services.GetRequiredService<GdiScreenCapturer>();
        host.Services.GetRequiredService<IScreenCapturer>().ShouldBeSameAs(service); await service.StopAsync();
        native.Calls.ShouldBeEmpty(); threads.All.ShouldBeEmpty(); desktop.Threads.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("capture")][InlineData("encoding")][InlineData("cleanup-failure")]
    public Task StopClosesCaptureAdmissionAndWaitsActualWork_StillDrainsSelectionOnFailure(string scenario) => ScreenCaptureTestScope.Run(f =>
        SelectionTestScope.Run(async selection =>
    {
        var order = new ConcurrentQueue<string>(); var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings());
        var ui = Substitute.For<IUiDispatcher>(); ui.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Func<Task>>()());
        var handler = Substitute.For<IActionHandler>(); handler.Action.Returns(IrisAction.InputTranslate);
        var dispatcher = new ActionDispatcher([handler], ui, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, dispatcher, NullLogger<TrayIconController>.Instance) { SuppressIconCreationForTesting = true };
        var host = Substitute.For<IHost>(); host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("host"); return Task.CompletedTask; });
        var presenter = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>(); presenter.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("presenter"); return Task.CompletedTask; });
        var bootstrapper = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), new ThemeService(settings), tray,
            new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host, NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0),
            settingsShutdownCoordinator: presenter, selectionReader: selection.Reader, actionDispatcher: dispatcher, screenCapturer: f.Service);
        var originalShutdown = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => order.Enqueue("shutdown");
        var gate = f.Gate(); Task? work = null, stop = null; Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            await selection.Clipboard.ReadVersionAsync();
            selection.Native.Before = stage => { if (stage == "Destroy") order.Enqueue("selection"); };
            if (scenario == "encoding")
            {
                f.Encoding.Configure = io => io.Before = stage => stage == "Flush" ? gate.WaitAsync() : Task.CompletedTask;
                var frame = ScreenCaptureTestScope.Frame(); work = f.Track(f.Service.CropAsync(frame, frame.VirtualBounds), typeof(OperationCanceledException));
            }
            else
            {
                f.Native.Before = stage => { if (stage == "BitBlt") gate.Block(); };
                if (scenario == "cleanup-failure") f.Native.Failures["ReleaseDC"] = -1;
                work = f.Track(f.Service.CaptureVirtualScreenAsync(CancellationToken.None), scenario == "cleanup-failure" ? typeof(IrisException) : typeof(OperationCanceledException));
            }
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop = bootstrapper.StopAsync(); stop.IsCompleted.ShouldBeFalse(); work.IsCompleted.ShouldBeFalse();
            Should.Throw<IrisException>(() => f.Service.CaptureVirtualScreenAsync(CancellationToken.None)).Detail!.ShouldContain("STOPPED");
            await dispatcher.DispatchAsync(IrisAction.InputTranslate, new(ActionTrigger.TrayMenu));
            await handler.DidNotReceive().ExecuteAsync(Arg.Any<ActionContext>(), Arg.Any<CancellationToken>());
            order.ShouldBeEmpty(); gate.Open();
            if (scenario == "cleanup-failure")
            {
                var error = await Should.ThrowAsync<IrisException>(() => stop); error.Detail!.ShouldContain("RELEASE_DC");
                order.ShouldBe(new[] { "selection" }); selection.Native.WindowAlive.ShouldBeFalse();
                f.Threads.All.Single().IsAlive.ShouldBeTrue(); f.Native.Failures.Clear(); stop = bootstrapper.StopAsync();
            }
            await stop;
            order.ShouldBe(new[] { "selection", "presenter", "host", "shutdown" });
            f.Native.Handles.ShouldBe(0); f.Threads.All.ShouldAllBe(t => !t.IsAlive);
            f.Encoding.Sessions.ShouldAllBe(io => !io.StreamOwned && io.InFlight == 0);
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            gate.Open(); f.Native.Failures.Clear();
            if (stop != null) try { await stop; } catch (Exception ex) { cleanup.Add(ex); }
            try { await bootstrapper.StopAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }));
}
