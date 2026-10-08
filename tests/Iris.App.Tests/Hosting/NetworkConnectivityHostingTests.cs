using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Iris.App.Hosting;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Hosting;

[Collection("AppShutdownerSharedState")]
public sealed class NetworkConnectivityHostingTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public Task CompleteHostDrainsStandaloneHeadAndRetainsCleanupFailureBeforeDispatcherExit(bool failCleanup) => WpfTestRunner.RunAsync(async () =>
    {
        using var dir = new TempDirectory(); var paths = new FakeAppPaths(dir.Path);
        await File.WriteAllTextAsync(paths.SettingsFile, JsonSerializer.Serialize(NetworkSettingsUiScope.Initial(), IrisJson.Options));
        var order = new ConcurrentQueue<string>(); using var send = new NetworkSettingsUiScope.Gate(); using var cleanupGate = new NetworkSettingsUiScope.Gate();
        var content = new NetworkSettingsUiScope.Content { Fail = failCleanup, BeforeDispose = cleanupGate.Pause };
        var factory = new HeadFactory(send, content);
        var hotkeys = Substitute.For<IHotkeyService>();
        hotkeys.Register(Arg.Any<IrisAction>(), Arg.Any<HotkeyBinding>()).Returns(c => new HotkeyRegistrationResult(c.Arg<IrisAction>(), true));
        var host = Iris.App.Tests.TestSupport.IsolatedAppHost.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(hotkeys); services.AddSingleton(Substitute.For<IHotkeyCaptureService>());
            services.AddSingleton(Substitute.For<IAutoStartService>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
            services.AddSingleton<ProxyConnectivityTransportFactory>(factory); services.AddSingleton<IHostedService>(new Hosted(order));
        });
        host.Services.GetRequiredService<TrayIconController>().SuppressIconCreationForTesting = true;
        var bootstrap = host.Services.GetRequiredService<AppBootstrapper>();
        var tester = host.Services.GetRequiredService<IProxyConnectivityTester>();
        var originalExit = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => order.Enqueue("dispatcher-exit");
        Task? stop = null; Task<ProxyConnectivityResult>? running = null;
        IProxyConnectivityTestSession? used = null, unused = null;
        Exception? primary = null; var errors = new List<Exception>();
        try
        {
            await bootstrap.StartAsync(); order.ToArray().ShouldBe(["host-start"]);
            var settings = host.Services.GetRequiredService<ISettingsService>().Current.Network;
            unused = tester.Create(new(settings, settings), new(NetworkEndpointKind.Translation, "same"));
            used = tester.Create(new(settings, settings), new(NetworkEndpointKind.Translation, "same"));
            running = used.RunAsync(); await send.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop = bootstrap.StopAsync(); stop.IsCompleted.ShouldBeFalse(); running.IsCompleted.ShouldBeFalse();
            Should.Throw<ObjectDisposedException>(() => tester.Create(new(settings, settings), new(NetworkEndpointKind.Translation, "same")));
            send.Open(); await cleanupGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.IsCompleted.ShouldBeFalse(); order.ShouldNotContain("host-stop"); order.ShouldNotContain("dispatcher-exit");
            cleanupGate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => running);
            if (failCleanup)
            {
                await Should.ThrowAsync<IrisException>(() => stop);
                order.ShouldNotContain("host-stop"); order.ShouldNotContain("dispatcher-exit");
                host.Services.GetRequiredService<ProxyConnectivityTester>().SessionCount.ShouldBe(1);
                content.Fail = false; stop = bootstrap.StopAsync();
            }
            await stop; order.ToArray().ShouldBe(["host-start", "host-stop", "dispatcher-exit"]);
            factory.Sends.ShouldBe(1); host.Services.GetRequiredService<ProxyConnectivityTester>().SessionCount.ShouldBe(0);
            Should.Throw<ObjectDisposedException>(() => unused.RunAsync());
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            send.Open(); cleanupGate.Open(); content.Fail = false;
            if (used != null) try { await used.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (unused != null) try { await unused.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (running != null) try { await running; } catch (OperationCanceledException) { } catch (Exception ex) { errors.Add(ex); }
            if (stop != null && !stop.IsCompleted) try { await stop; } catch (Exception ex) { errors.Add(ex); }
            try { await bootstrap.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            AppBootstrapper.AppShutdowner = originalExit;
            try { await ((IAsyncDisposable)host).DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });

    private sealed class HeadFactory(NetworkSettingsUiScope.Gate send, HttpContent content) : ProxyConnectivityTransportFactory
    {
        internal int Sends;
        internal override HttpMessageHandler Create(NetworkRoute route) => new Handler(this, send, content);
        private sealed class Handler(HeadFactory owner, NetworkSettingsUiScope.Gate gate, HttpContent content) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { owner.Sends++; request.Method.ShouldBe(HttpMethod.Head); await gate.PauseAsync(); return new(HttpStatusCode.OK) { Content = content }; }
        }
    }
    private sealed class Hosted(ConcurrentQueue<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { order.Enqueue("host-start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { order.Enqueue("host-stop"); return Task.CompletedTask; }
    }
}
