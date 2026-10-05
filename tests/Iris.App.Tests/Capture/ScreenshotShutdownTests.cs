using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ScreenshotShutdownTests
{
    [Fact]
    public Task ActualBootstrapperRetainsOverlayDependenciesUntilRetry() => ScreenshotTestScope.Run(async f =>
    {
        await f.A.OpenSettings(); var settingsWindow = f.A.Settings.ActiveSession!.Window;
        var request = f.Track(f.Service.CaptureRegionAsync(ScreenshotPurpose.Translate, default), typeof(IrisException));
        await f.Ready(); var overlay = f.Service.Windows.Single();
        f.Native.FailClose = true; f.Select(); await Should.ThrowAsync<IrisException>(() => request);
        f.Codec.Native.Failures["ReleaseDC"] = -1;
        await Should.ThrowAsync<IrisException>(() => f.Codec.Service.CaptureVirtualScreenAsync(CancellationToken.None));
        await f.A.Mouse.Platform.Clipboard.ReadVersionAsync();
        int hostStops = 0, exits = 0;
        var host = Substitute.For<IHost>(); host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { hostStops++; return Task.CompletedTask; });
        var settings = f.A.Mouse.Settings;
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, f.A.Mouse.Dispatcher, NullLogger<TrayIconController>.Instance)
        { SuppressIconCreationForTesting = true };
        var bootstrap = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance),
            f.A.Mouse.Services.GetRequiredService<ThemeService>(), tray, f.A.Mouse.Services.GetRequiredService<TrayNotificationService>(),
            host, NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0), settingsShutdownCoordinator: f.A.Settings,
            translateShutdownCoordinator: f.A.Mouse.Presenter, actionDispatcher: f.A.Mouse.Dispatcher, recordingCoordinator: f.A.Recording,
            selectionReader: f.A.Mouse.Platform.Reader, selectionReadCoordinator: f.A.Mouse.Reader, selectionIconController: f.A.Mouse.Controller,
            screenCapturer: f.Codec.Service, screenshotPreparation: f.A.Preparation, screenshotService: f.Service);
        var previous = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => exits++;
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            var error = await Should.ThrowAsync<IrisException>(() => bootstrap.StopAsync());
            error.Detail!.ShouldContain("SCREENSHOT"); error.Detail!.ShouldContain("SCREEN[");
            f.Service.HasPendingOverlays.ShouldBeTrue(); overlay.Handle.ShouldNotBe(0); overlay.FrozenPixels.ShouldNotBeNull();
            settingsWindow.IsVisible.ShouldBeFalse(); f.A.Settings.ActiveSession.ShouldNotBeNull();
            hostStops.ShouldBe(0); exits.ShouldBe(0);
            f.A.Mouse.Platform.Native.WindowAlive.ShouldBeFalse(); // Independent selection cleanup still ran.
            f.Native.FailClose = false; f.Codec.Native.Failures.Clear(); await bootstrap.StopAsync();
            overlay.IsClosed.ShouldBeTrue(); overlay.Handle.ShouldBe(0); overlay.FrozenPixels.ShouldBeNull();
            f.A.Settings.ActiveSession.ShouldBeNull(); f.Service.HasPendingOverlays.ShouldBeFalse();
            hostStops.ShouldBe(1); exits.ShouldBe(1);
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            f.Native.FailClose = false; f.Codec.Native.Failures.Clear();
            try { await bootstrap.StopAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            AppBootstrapper.AppShutdowner = previous;
        }
        if (primary != null && cleanup.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }, gdi: true);

    [Fact]
    public Task ActualHostRegistersSameLazyScreenshotPort() => ScreenshotTestScope.Run(async f =>
    {
        var paths = Substitute.For<Iris.Core.Storage.IAppPaths>(); paths.DataDirectory.Returns(Path.GetTempPath());
        using var host = AppHostBuilder.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton<Iris.Core.Settings.ISettingsService>(f.A.Mouse.Settings);
            services.AddSingleton<Iris.Core.Platform.IDesktopContext>(f.A.Mouse.Platform.Desktop);
            services.AddSingleton<Iris.Core.Platform.IScreenCapturer>(f.Codec.Service);
            services.AddSingleton<Iris.Core.Platform.IClipboardService>(f.Clipboard);
            services.AddSingleton<Iris.App.Capture.IScreenshotSystem>(f.Native);
            services.AddSingleton(f.A.Preparation); services.AddSingleton(f.A.Mouse.Presenter); services.AddSingleton(f.A.Settings);
        });
        var service = host.Services.GetRequiredService<Iris.App.Capture.ScreenshotService>();
        host.Services.GetRequiredService<IScreenshotService>().ShouldBeSameAs(service);
        await service.StopAsync(); f.Native.Flushes.ShouldBe(0); f.Native.Windows.ShouldBeEmpty(); f.Codec.Native.Calls.ShouldBeEmpty();
    });
}
