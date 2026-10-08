using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Imaging;
using Iris.Platform.Windows.Tests.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class ImagePreprocessorShutdownTests
{
    [Fact]
    public Task ActualHost_RegistersSameLazyPreprocessor() => ScreenshotTestScope.Run(async f =>
    {
        var paths = Substitute.For<IAppPaths>(); paths.DataDirectory.Returns(Path.GetTempPath());
        using var host = Iris.App.Tests.TestSupport.IsolatedAppHost.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton<Iris.Core.Settings.ISettingsService>(f.A.Mouse.Settings);
            services.AddSingleton<Iris.Core.Platform.IDesktopContext>(f.A.Mouse.Platform.Desktop);
        });
        var service = host.Services.GetRequiredService<WinRtImagePreprocessor>();
        host.Services.GetRequiredService<IImagePreprocessor>().ShouldBeSameAs(service);
        await service.StopAsync();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task ActualBootstrapper_WaitsForCodecAndRetriesBeforeHostShutdown(bool failCleanup) => ScreenshotTestScope.Run(f =>
        ImagePreparationTestScope.Run(async p =>
        {
            var gate = p.Gate("E.Flush");
            if (failCleanup) p.Failures["E.Stream"] = new IOException("private stream");
            var input = await ImagePreparationTestScope.Image();
            var request = p.Start(input, ImagePreparationTestScope.Constraints(formats: [ImageFormat.Bmp]),
                failCleanup ? typeof(IrisException) : typeof(OperationCanceledException));
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            await f.A.Mouse.Platform.Clipboard.ReadVersionAsync();
            int hostStops = 0, exits = 0;
            var host = Substitute.For<IHost>(); host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { hostStops++; return Task.CompletedTask; });
            var settings = f.A.Mouse.Settings;
            var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, f.A.Mouse.Dispatcher, NullLogger<TrayIconController>.Instance)
            { SuppressIconCreationForTesting = true };
            var bootstrap = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
                new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance),
                f.A.Mouse.Services.GetRequiredService<ThemeService>(), tray, f.A.Mouse.Services.GetRequiredService<TrayNotificationService>(),
                host, NullLogger<AppBootstrapper>.Instance, new AppStartupInfo(0),
                selectionReader: f.A.Mouse.Platform.Reader, screenCapturer: f.Codec.Service,
                screenshotPreparation: f.A.Preparation, screenshotService: f.Service, imagePreprocessor: p.Service);
            var previous = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => exits++;
            Exception? primary = null; var errors = new List<Exception>();
            try
            {
                var stop = bootstrap.StopAsync(); stop.IsCompleted.ShouldBeFalse(); hostStops.ShouldBe(0);
                Should.Throw<IrisException>(() => p.Service.PrepareAsync(input, ImagePreparationTestScope.Constraints(), default));
                gate.Release();
                if (failCleanup)
                {
                    (await Should.ThrowAsync<IrisException>(() => stop)).Detail!.ShouldContain("IMAGE_PREPROCESSOR");
                    await Should.ThrowAsync<IrisException>(() => request);
                    hostStops.ShouldBe(0); exits.ShouldBe(0);
                    f.A.Mouse.Platform.Native.WindowAlive.ShouldBeFalse();
                    p.Failures.Clear(); await bootstrap.StopAsync();
                }
                else { await stop; await Should.ThrowAsync<OperationCanceledException>(() => request); }
                p.Encoders.Single().StreamDisposed.ShouldBeTrue(); hostStops.ShouldBe(1); exits.ShouldBe(1);
            }
            catch (Exception ex) { primary = ex; }
            finally
            {
                gate.Release(); p.Failures.Clear();
                try { await bootstrap.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
                AppBootstrapper.AppShutdowner = previous;
            }
            if (primary != null && errors.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) errors.Insert(0, primary);
            if (errors.Count != 0) throw new AggregateException(errors);
        }));
}
