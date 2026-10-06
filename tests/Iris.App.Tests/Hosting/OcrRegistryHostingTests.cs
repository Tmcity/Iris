using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using Iris.App.Services;
using Iris.App.Tests.Capture;
using Iris.App.Tray;
using Iris.App.Hosting;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Hosting;

[Collection("AppShutdownerSharedState")]
public sealed class OcrRegistryHostingTests
{
    [Theory]
    [InlineData("normal")][InlineData("owner-failure")][InlineData("early-failure")]
    public Task ActualWindowOwnerAndGenericHost_DrainRegistryBeforeHost(string scenario) => PreparationTestScope.Run(async f =>
    {
        var order = new ConcurrentQueue<string>();
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { Ocr = new() { Providers = [new() { Id = "local", DisplayName = "Local", Kind = OcrProviderKind.WindowsLocal }] } });
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("load"); return Task.CompletedTask; });
        var translations = Substitute.For<ITranslationProviderRegistry>();
        translations.RefreshAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Enqueue("translation"); return Task.CompletedTask; });
        var factory = new Factory(order);
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddIrisCore(); services.AddSingleton(settings); services.AddSingleton(Substitute.For<ISecretStore>());
            services.AddSingleton<IOcrProviderFactory>(factory); services.AddSingleton<IHostedService>(new HostService(order));
        }).Build();
        var registry = host.Services.GetRequiredService<IOcrProviderRegistry>();
        var capture = Substitute.For<IHotkeyCaptureService>(); bool failEarly = scenario == "early-failure";
        capture.StopAsync().Returns(_ => failEarly ? Task.FromException(new IOException("private capture")) : Task.CompletedTask);
        var tray = new TrayIconController(Substitute.For<IAppLifetime>(), settings, Substitute.For<IActionDispatcher>(), NullLogger<TrayIconController>.Instance)
        { SuppressIconCreationForTesting = true };
        var bootstrap = new AppBootstrapper(settings, translations, Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), new ThemeService(settings),
            tray, new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host, NullLogger<AppBootstrapper>.Instance, new(0),
            settingsShutdownCoordinator: f.Settings, captureService: capture, ocrRegistry: registry);
        var originalShutdown = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => order.Enqueue("shutdown");
        IOcrProviderSnapshot? lease = null; Task? stop = null, released = null;
        bool failOwner = scenario == "owner-failure"; System.Windows.Window? window = null;
        CancelEventHandler closing = (_, args) => { if (failOwner) args.Cancel = true; };
        Exception? primary = null; var errors = new List<Exception>();
        try
        {
            await bootstrap.StartAsync();
            order.Take(4).ShouldBe(new[] { "load", "translation", "ocr", "host-start" });
            lease = await registry.AcquireSnapshotAsync();
            await f.OpenSettings(); window = f.Settings.ActiveSession!.Window;
            window.Closing += closing;
            window.Closed += (_, _) => { order.Enqueue("owner-closed"); released = lease.DisposeAsync().AsTask(); };
            factory.Provider.Hold = scenario == "normal";
            stop = bootstrap.StopAsync();
            await Should.ThrowAsync<ObjectDisposedException>(() => registry.AcquireSnapshotAsync());
            if (scenario == "normal")
            {
                await factory.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                stop.IsCompleted.ShouldBeFalse(); order.ShouldNotContain("host-stop");
                factory.Provider.Release.TrySetResult();
            }
            if (failOwner)
            {
                var error = await Should.ThrowAsync<IrisException>(() => stop.WaitAsync(TimeSpan.FromSeconds(10)));
                error.Detail!.ShouldContain("SETTINGS_OWNER"); error.Detail!.ShouldContain("OCR_PENDING_OWNER");
                order.ShouldNotContain("host-stop"); factory.Provider.Disposed.ShouldBeFalse();
                lease.Providers.Count.ShouldBe(1); f.Settings.ActiveSession.ShouldNotBeNull();
                failOwner = false; stop = bootstrap.StopAsync();
            }
            if (failEarly)
            {
                var error = await Should.ThrowAsync<IrisException>(() => stop.WaitAsync(TimeSpan.FromSeconds(10)));
                error.Detail!.ShouldContain("RECORDING"); error.ToString().ShouldNotContain("private capture");
                f.Settings.ActiveSession.ShouldBeNull(); factory.Provider.Disposed.ShouldBeTrue();
                order.ShouldNotContain("host-stop"); failEarly = false; stop = bootstrap.StopAsync();
            }
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            if (released != null) await released;
            var events = order.ToArray();
            Array.IndexOf(events, "owner-closed").ShouldBeLessThan(Array.IndexOf(events, "ocr-dispose"));
            Array.IndexOf(events, "ocr-dispose").ShouldBeLessThan(Array.IndexOf(events, "host-stop"));
            events.Last().ShouldBe("shutdown"); factory.Provider.SyncCalls.ShouldBe(0);
            host.Dispose(); factory.Provider.DisposeCalls.ShouldBe(1);
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            factory.Provider.Release.TrySetResult();
            failEarly = failOwner = false; if (window != null) window.Closing -= closing;
            try { await f.Settings.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (released != null) try { await released; } catch (Exception ex) { errors.Add(ex); }
            if (lease != null) try { await lease.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (stop != null && !stop.IsCompleted) try { await stop; } catch (Exception ex) { errors.Add(ex); }
            try { await bootstrap.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });

    private sealed class HostService(ConcurrentQueue<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { order.Enqueue("host-start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { order.Enqueue("host-stop"); return Task.CompletedTask; }
    }
    private sealed class Factory(ConcurrentQueue<string> order) : IOcrProviderFactory
    {
        internal Provider Provider { get; } = new(order);
        public OcrProviderKind Kind => OcrProviderKind.WindowsLocal;
        public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials) { order.Enqueue("ocr"); return Provider; }
    }
    private sealed class Provider(ConcurrentQueue<string> order) : IOcrProvider, IAsyncDisposable, IDisposable
    {
        internal bool Disposed; internal int DisposeCalls, SyncCalls;
        internal bool Hold;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderDescriptor Descriptor { get; } = new("local", "Local", "WindowsLocal", null, true);
        public ImageConstraints Constraints { get; } = new(10000, false, 100, 40, [ImageFormat.Png]);
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero));
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++; Entered.TrySetResult();
            if (Hold) await Release.Task;
            Disposed = true; order.Enqueue("ocr-dispose");
        }
        public void Dispose() { SyncCalls++; throw new InvalidOperationException("Prefer async."); }
    }
}
