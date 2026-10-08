using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.Capture;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Hosting;

[Collection("AppShutdownerSharedState")]
public sealed class NetworkHostingTests
{
    [Fact]
    public Task FailedRealMigration_DoesNotPublishNetworkOrStartHostAndTray() => WpfTestRunner.RunAsync(async () =>
    {
        using var dir = new TempDirectory(); var paths = new FakeAppPaths(dir.Path); var order = new ConcurrentQueue<string>();
        const string original = "{\"schemaVersion\":1,\"network\":{\"proxyMode\":\"custom\",\"customProxyUrl\":\"http://synthetic:private@bad host.invalid/\"}}";
        await File.WriteAllTextAsync(paths.SettingsFile, original, TestContext.Current.CancellationToken);
        var secrets = Substitute.For<ISecretStore>();
        var host = AppHostBuilder.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton(secrets); services.AddSingleton(Substitute.For<IHotkeyService>()); services.AddSingleton(Substitute.For<IHotkeyCaptureService>());
            services.AddSingleton(Substitute.For<IAutoStartService>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
            services.AddSingleton<INetworkConfigurationObserver>(new Observe(order)); services.AddSingleton<IHostedService>(new Hosted(order));
        });
        var tray = host.Services.GetRequiredService<TrayIconController>(); tray.SuppressIconCreationForTesting = true;
        var oldFatal = AppBootstrapper.FatalErrorShower; var oldExit = AppBootstrapper.AppShutdowner;
        AppBootstrapper.FatalErrorShower = (_, message) => { message.ShouldNotContain("private"); order.Enqueue("fatal"); };
        AppBootstrapper.AppShutdowner = code => { code.ShouldBe(1); order.Enqueue("exit"); };
        try
        {
            await host.Services.GetRequiredService<AppBootstrapper>().StartAsync();
            order.ToArray().ShouldBe(["fatal", "exit"]); tray.WasInitialized.ShouldBeFalse();
            secrets.DidNotReceive().Get(Arg.Any<string>()); secrets.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>());
            (await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken)).ShouldBe(original);
            File.Exists(paths.SettingsFile + ".bak").ShouldBeFalse();
        }
        finally
        {
            AppBootstrapper.FatalErrorShower = oldFatal; AppBootstrapper.AppShutdowner = oldExit;
            // Generic Host owns async-disposable services. Do not synchronously block this test dispatcher.
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task CompleteHost_PreparesNetworkBeforeProviderConstruction_AndDrainsWindowThenNetworkBeforeHost(bool failContent) => PreparationTestScope.Run(async f =>
    {
        using var dir = new TempDirectory(); var paths = new FakeAppPaths(dir.Path); var order = new ConcurrentQueue<string>();
        var initial = new AppSettings
        {
            General = new() { Theme = AppTheme.Light, ShowStartupNotification = false }, Network = new() { ProxyMode = ProxyMode.None },
            Translation = new() { Providers = [new() { Id = "anthropic", Kind = TranslationProviderKind.Anthropic, DisplayName = "synthetic", BaseUrl = "http://origin.example.test", Model = "synthetic", Enabled = true }] }
        };
        await File.WriteAllTextAsync(paths.SettingsFile, JsonSerializer.Serialize(initial, IrisJson.Options));
        var secrets = Substitute.For<ISecretStore>();
        var hotkeys = Substitute.For<IHotkeyService>();
        hotkeys.Register(Arg.Any<Iris.Core.Actions.IrisAction>(), Arg.Any<HotkeyBinding>())
            .Returns(call => new HotkeyRegistrationResult(call.Arg<Iris.Core.Actions.IrisAction>(), true));
        using var host = AppHostBuilder.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), Environment.TickCount64, services =>
        {
            services.AddSingleton(secrets); services.AddSingleton(hotkeys); services.AddSingleton(Substitute.For<IHotkeyCaptureService>());
            services.AddSingleton(Substitute.For<IAutoStartService>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
            services.AddSingleton<ISettingsShutdownCoordinator>(f.Settings);
            services.AddSingleton<INetworkConfigurationObserver>(new Observe(order));
            services.AddSingleton<IHostedService>(new Hosted(order));
        });
        var services = host.Services;
        services.GetRequiredService<TrayIconController>().SuppressIconCreationForTesting = true;
        var source = services.GetRequiredService<INetworkProxySnapshotSource>();
        source.ShouldBeSameAs(services.GetRequiredService<ISettingsEditCoordinator>());
        services.GetRequiredService<IProxyConnectivityTester>().ShouldBeSameAs(services.GetRequiredService<ProxyConnectivityTester>());
        var lifetime = services.GetRequiredService<INetworkTransportLifetime>();
        var bootstrap = services.GetRequiredService<AppBootstrapper>();
        var originalShutdown = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => order.Enqueue("app-exit");
        var leaf = new FailureContent { Fail = failContent }; HttpContent? owned = null; IDisposable? lease = null; Task? stop = null;
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            order.ShouldBeEmpty(); await bootstrap.StartAsync();
            order.ToArray().ShouldBe(["network", "host-start"]);
            using (var providers = await services.GetRequiredService<ITranslationProviderRegistry>().AcquireSnapshotAsync())
                providers.Providers.Single().Provider.GetType().Name.ShouldBe("AnthropicTranslationProvider");
            var snapshot = await source.CaptureCurrentAsync(new("http://origin.example.test/"));
            // Test-only reflection reaches internal ownership without publishing another Core port or opening a socket.
            var manager = services.GetRequiredService<NetworkTransportManager>();
            var managerType = manager.GetType();
            lease = (IDisposable)managerType.GetMethod("Acquire", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [HttpClientNames.Llm, snapshot, null])!;
            owned = (HttpContent)managerType.GetMethod("OwnContent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [lease, leaf])!;
            lease.Dispose();
            await f.OpenSettings(); var window = f.Settings.ActiveSession!.Window;
            window.Closed += (_, _) =>
            {
                order.Enqueue("owner-closed");
                try { owned.Dispose(); } catch (IrisException) { order.Enqueue("content-pending"); }
            };
            stop = bootstrap.StopAsync();
            if (failContent)
            {
                await Should.ThrowAsync<IrisException>(() => stop); order.ShouldNotContain("host-stop"); order.ShouldNotContain("app-exit");
                leaf.Disposed.ShouldBeFalse(); leaf.Fail = false; stop = bootstrap.StopAsync();
            }
            await stop; leaf.Disposed.ShouldBeTrue();
            var events = order.ToArray(); Array.IndexOf(events, "owner-closed").ShouldBeLessThan(Array.IndexOf(events, "host-stop"));
            events.Last().ShouldBe("app-exit");
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            leaf.Fail = false;
            try { await f.Settings.ShutdownAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            try { owned?.Dispose(); lease?.Dispose(); } catch (Exception ex) { cleanup.Add(ex); }
            if (stop != null && !stop.IsCompleted) try { await stop; } catch (Exception ex) { cleanup.Add(ex); }
            try { await bootstrap.StopAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            AppBootstrapper.AppShutdowner = originalShutdown;
        }
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    });

    private sealed class Observe(ConcurrentQueue<string> order) : INetworkConfigurationObserver
    {
        public Task ApplyAsync(NetworkSettings settings, Guid generationId, CancellationToken cancellationToken = default) { order.Enqueue("network"); return Task.CompletedTask; }
    }
    private sealed class Hosted(ConcurrentQueue<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { order.Enqueue("host-start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { order.Enqueue("host-stop"); return Task.CompletedTask; }
    }
    private sealed class FailureContent : HttpContent
    {
        internal bool Fail, Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { if (disposing) { if (Fail) throw new IOException("synthetic-cleanup"); Disposed = true; } base.Dispose(disposing); }
    }
}
