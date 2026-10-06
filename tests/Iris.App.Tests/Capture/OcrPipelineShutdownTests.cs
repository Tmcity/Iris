using System.Runtime.ExceptionServices;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tray;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.Tests.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Capture;

[Collection("ScreenshotRendering")]
public sealed class OcrPipelineShutdownTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public Task ActualServiceRegistryCodecAndBootstrapper_DrainInDependencyOrder(bool failDispose) => ImagePreparationTestScope.Run(async p =>
    {
        var settings = Substitute.For<ISettingsService>();
        var initial = new AppSettings { Ocr = new() { ActiveProviderId = "local", Providers = [new() { Id = "local", DisplayName = "Local", Kind = OcrProviderKind.WindowsLocal }] } };
        settings.Current.Returns(initial);
        var factory = new Factory(); var images = new Images(p.Service);
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddIrisCore(); services.AddSingleton(settings); services.AddSingleton(Substitute.For<ISecretStore>());
            services.AddSingleton<IImagePreprocessor>(images); services.AddSingleton<IOcrProviderFactory>(factory);
        }).Build();
        var registry = host.Services.GetRequiredService<IOcrProviderRegistry>();
        var service = host.Services.GetRequiredService<IOcrService>();
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, Substitute.For<Iris.Core.Actions.IActionDispatcher>(), NullLogger<TrayIconController>.Instance)
        { SuppressIconCreationForTesting = true };
        int hostStops = 0, exits = 0;
        var hostBoundary = Substitute.For<IHost>(); hostBoundary.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { hostStops++; return Task.CompletedTask; });
        var bootstrap = new AppBootstrapper(settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), new ThemeService(settings),
            tray, new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), hostBoundary, NullLogger<AppBootstrapper>.Instance, new(0),
            imagePreprocessor: p.Service, ocrRegistry: registry, ocrService: service);
        var previous = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => exits++;
        var gate = p.Gate("E.Flush"); var input = await ImagePreparationTestScope.Image(40, 40);
        Task? work = null, stopping = null; Exception? primary = null; var errors = new List<Exception>();
        try
        {
            work = service.RecognizeAsync(input, cancellationToken: CancellationToken.None);
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            // Retire while leased so Service, not Refresh, owns the last release.
            settings.Current.Returns(initial with { Ocr = new() { Providers = [] } });
            settings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(settings, new SettingsChangedEventArgs(initial, settings.Current));
            await registry.RefreshAsync(CancellationToken.None); factory.Provider.Fail = failDispose;
            stopping = bootstrap.StopAsync(); await images.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stopping.IsCompleted.ShouldBeFalse(); work.IsCompleted.ShouldBeFalse(); hostStops.ShouldBe(0);
            await Should.ThrowAsync<ObjectDisposedException>(() => service.RecognizeAsync(input, cancellationToken: CancellationToken.None));
            // Admission is still open in the dependency until OCR finishes. Queue then cancel an independent request.
            using var probeCancel = new CancellationTokenSource();
            var probe = p.Start(input, ImagePreparationTestScope.Constraints(), typeof(OperationCanceledException), probeCancel.Token);
            probeCancel.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => probe);
            gate.Release();
            if (failDispose)
            {
                await Should.ThrowAsync<IrisException>(() => work); await Should.ThrowAsync<IrisException>(() => stopping);
                hostStops.ShouldBe(0); exits.ShouldBe(0); factory.Provider.Disposed.ShouldBeFalse();
                // The failed service must not cause premature preprocessor shutdown.
                await p.Start(input, ImagePreparationTestScope.Constraints());
                factory.Provider.Fail = false; stopping = bootstrap.StopAsync();
            }
            else await Should.ThrowAsync<OperationCanceledException>(() => work);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            factory.Provider.Disposed.ShouldBeTrue(); factory.Provider.SyncCalls.ShouldBe(0); factory.Provider.Recognitions.ShouldBe(0);
            p.InFlight.ShouldBe(0); p.Encoders.ShouldAllBe(e => e.StreamDisposed);
            Should.Throw<IrisException>(() => p.Service.PrepareAsync(input, ImagePreparationTestScope.Constraints(), CancellationToken.None));
            hostStops.ShouldBe(1); exits.ShouldBe(1); host.Dispose();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            gate.Release(); factory.Provider.Fail = false;
            if (work != null) try { await work; } catch (OperationCanceledException) { } catch (IrisException) when (failDispose) { } catch (Exception ex) { errors.Add(ex); }
            if (stopping != null) try { await stopping; } catch (IrisException) when (failDispose) { } catch (Exception ex) { errors.Add(ex); }
            try { await bootstrap.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            await images.Registration.DisposeAsync(); AppBootstrapper.AppShutdowner = previous;
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
    private sealed class Images(IImagePreprocessor actual) : IImagePreprocessor
    {
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        public Task<ImageData> PrepareAsync(ImageData image, ImageConstraints constraints, CancellationToken cancellationToken)
        { Registration = cancellationToken.Register(() => Canceled.TrySetResult()); return actual.PrepareAsync(image, constraints, cancellationToken); }
    }
    private sealed class Factory : IOcrProviderFactory
    {
        internal Provider Provider { get; } = new();
        public OcrProviderKind Kind => OcrProviderKind.WindowsLocal;
        public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials) => Provider;
    }
    private sealed class Provider : IOcrProvider, IAsyncDisposable, IDisposable
    {
        internal bool Fail, Disposed; internal int SyncCalls, Recognitions;
        public ProviderDescriptor Descriptor { get; } = new("local", "Local", "WindowsLocal", null, true);
        public ImageConstraints Constraints { get; } = new(100000, false, 100, 40, [ImageFormat.Bmp]);
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        { Recognitions++; return Task.FromResult(new OcrResult("local", [new("hello")], null, TimeSpan.Zero)); }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { if (Fail) throw new IOException("private provider"); Disposed = true; return ValueTask.CompletedTask; }
        public void Dispose() { SyncCalls++; throw new InvalidOperationException("Use async."); }
    }
}
