using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Tests.Capture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("AppShutdownerSharedState")]
public sealed class PreparationShutdownTests
{
    [Fact]
    public Task ActualBootstrapper_PreservesIndependentErrorsAndRetriesBeforeDestroyingOwners() => PreparationTestScope.Run(f =>
        ScreenCaptureTestScope.Run(async capture =>
    {
        await f.OpenSettings();
        var window = f.Settings.ActiveSession!.Window;
        await f.Mouse.Mode(Iris.Core.Settings.MouseSelectionMode.ShowIcon);
        await f.Mouse.Controller.StartAsync(); await f.Mouse.ShowCandidate();
        f.Mouse.Icon.FailHide = true;
        await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true));
        f.Mouse.Icon.FailClose = true;
        capture.Native.Failures["ReleaseDC"] = -1;
        await Should.ThrowAsync<IrisException>(() => capture.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        await f.Mouse.Platform.Clipboard.ReadVersionAsync();
        int hostStops = 0, shutdowns = 0;
        var host = Substitute.For<IHost>();
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { hostStops++; return Task.CompletedTask; });
        var settings = f.Mouse.Settings;
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, f.Mouse.Dispatcher,
            NullLogger<TrayIconController>.Instance) { SuppressIconCreationForTesting = true };
        var bootstrap = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance),
            f.Mouse.Services.GetRequiredService<ThemeService>(), tray, f.Mouse.Services.GetRequiredService<TrayNotificationService>(),
            host, NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0),
            settingsShutdownCoordinator: f.Settings, translateShutdownCoordinator: f.Mouse.Presenter,
            actionDispatcher: f.Mouse.Dispatcher, recordingCoordinator: f.Recording,
            selectionReader: f.Mouse.Platform.Reader, selectionReadCoordinator: f.Mouse.Reader,
            selectionIconController: f.Mouse.Controller, screenCapturer: capture.Service, screenshotPreparation: f.Preparation);
        var previous = AppBootstrapper.AppShutdowner;
        AppBootstrapper.AppShutdowner = _ => shutdowns++;
        try
        {
            var error = await Should.ThrowAsync<IrisException>(() => bootstrap.StopAsync());
            error.Detail!.ShouldContain("PREPARATION"); error.Detail!.ShouldContain("SCREEN"); error.Detail!.ShouldContain("MOUSE");
            hostStops.ShouldBe(0); shutdowns.ShouldBe(0); f.Settings.ActiveSession!.IsOpen.ShouldBeTrue();
            f.Mouse.Platform.Native.WindowAlive.ShouldBeFalse();
            f.Mouse.Icon.FailClose = f.Mouse.Icon.FailHide = false; capture.Native.Failures.Clear();
            await bootstrap.StopAsync();
            f.Settings.ActiveSession.ShouldBeNull(); hostStops.ShouldBe(1); shutdowns.ShouldBe(1);
            new System.Windows.Interop.WindowInteropHelper(window).Handle.ShouldBe(0);
            f.Recording.CanStartRecording.ShouldBeFalse(); capture.Native.Handles.ShouldBe(0);
            capture.Threads.All.ShouldAllBe(thread => !thread.IsAlive);
            f.Mouse.Mouse.ActiveHandles.ShouldBe(0); f.Mouse.Mouse.MessageThread!.IsAlive.ShouldBeFalse();
        }
        finally
        {
            f.Mouse.Icon.FailClose = f.Mouse.Icon.FailHide = false; capture.Native.Failures.Clear();
            try { await bootstrap.StopAsync(); }
            finally { AppBootstrapper.AppShutdowner = previous; }
        }
    }));
}
