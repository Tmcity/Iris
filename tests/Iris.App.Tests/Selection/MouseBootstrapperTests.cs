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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Selection;

[Collection("AppShutdownerSharedState")]
public sealed class MouseBootstrapperTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task ActualExit_WaitsControllerRestoreBeforeCompositeWindowAndHost(bool injectFailure) => MouseInteractionTestScope.Run(async f =>
    {
        var order = new ConcurrentQueue<string>();
        var host = Substitute.For<IHost>(); host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("host"); return Task.CompletedTask; });
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), f.Settings, f.Dispatcher, NullLogger<TrayIconController>.Instance) { SuppressIconCreationForTesting = true };
        var presenter = Substitute.For<Iris.App.Settings.ISettingsShutdownCoordinator>();
        presenter.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("settings"); return Task.CompletedTask; });
        var bootstrapper = new AppBootstrapper(f.Settings, f.Services.GetRequiredService<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(f.Settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), f.Services.GetRequiredService<ThemeService>(),
            tray, f.Services.GetRequiredService<TrayNotificationService>(), host, NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0),
            settingsShutdownCoordinator: presenter, translateShutdownCoordinator: f.Presenter, selectionReader: f.Platform.Reader,
            actionDispatcher: f.Dispatcher, selectionReadCoordinator: f.Reader, selectionIconController: f.Controller);
        var original = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => order.Enqueue("shutdown");
        Task? stop = null; Exception? primary = null; var cleanup = new List<Exception>();
        var injected = new InvalidOperationException("injected body after restore entered");
        try
        {
            f.Platform.Seed();
            await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { MouseMode = MouseSelectionMode.ShowIcon, ReadOrder = [SelectionReadMethod.Clipboard] } });
            await f.Controller.StartAsync(); await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
            await f.Platform.Time.WaitForDelayAsync(15); var gate = f.Platform.NativeGate("Set");
            stop = bootstrapper.StopAsync();
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); stop.IsCompleted.ShouldBeFalse(); order.ShouldBeEmpty();
            Should.Throw<IrisException>(() => f.Platform.Reader.ReadAsync(new([], true, 100, []), CancellationToken.None));
            if (injectFailure) throw injected;
            gate.Release.Set(); await stop;
            f.Mouse.ActiveHandles.ShouldBe(0); f.Platform.Native.WindowAlive.ShouldBeFalse();
            order.ToArray().ShouldBe(new[] { "settings", "host", "shutdown" });
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var gate in f.Platform.Gates) gate.Set();
            if (stop != null) try { await stop; } catch (Exception ex) { cleanup.Add(ex); }
            try { await bootstrapper.StopAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            AppBootstrapper.AppShutdowner = original;
        }
        if (injectFailure && ReferenceEquals(primary, injected)) primary = null;
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        cleanup.ShouldBeEmpty(); f.Mouse.ActiveHandles.ShouldBe(0); f.Platform.Native.WindowAlive.ShouldBeFalse();
    });
}
