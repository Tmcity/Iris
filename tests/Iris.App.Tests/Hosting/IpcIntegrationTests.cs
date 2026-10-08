using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Hosting;

[Collection("AppShutdownerSharedState")]
public sealed class IpcIntegrationTests
{
    [Fact]
    public Task UnstartedCoordinatorAndHostDisposeSynchronouslyWithoutOpeningPipe() => Run(f =>
    {
        f.Ipc.Dispose(); f.Ipc.Dispose(); f.Host.Dispose();
        f.Factory.Creates.ShouldBe(0); f.HostAlreadyDisposed = true;
        return Task.CompletedTask;
    });

    [Fact]
    public Task HostStartupFailureBeforeIpcOpensLeavesCoordinatorSynchronouslyDisposable() => Run(async f =>
    {
        f.FailHostStart = true;
        await Should.ThrowAsync<IOException>(() => f.Bootstrap.StartAsync());
        f.Ipc.Dispose(); f.Factory.Creates.ShouldBe(0); f.Shown.ShouldBeEmpty();
    }, new(IrisAction.TranslateText, "never execute"));
    [Fact]
    public Task ReadyHostExecutesInitialTextOnce_AndNeverAddsTextToConfiguration() => Run(async f =>
    {
        f.Factory.Creates.ShouldBe(0);
        await f.Bootstrap.StartAsync(); await f.Ipc.WaitForDispatchesAsync();
        f.Shown.ShouldHaveSingleItem(); f.Shown.Single().Text.ShouldBe("  initial 中文\ntext  ");
        f.Shown.Single().Origin.ShouldBe(TextOrigin.CommandLine); f.Shown.Single().Anchor.ShouldBeNull();
        await f.Bootstrap.StartAsync(); f.Shown.Count.ShouldBe(1); f.Factory.Creates.ShouldBe(1);
        f.Host.Services.GetRequiredService<IConfiguration>().AsEnumerable().ShouldNotContain(pair => pair.Value != null && pair.Value.Contains("initial", StringComparison.Ordinal));
    }, new(IrisAction.TranslateText, "  initial 中文\ntext  "));

    [Fact]
    public Task RealPipeUsesActualDispatcherAndScreenshotTranslateHandler() => Run(async f =>
    {
        await f.Bootstrap.StartAsync(); f.Shown.ShouldBeEmpty();
        (await f.Send(new(IrisAction.ScreenshotTranslate))).ShouldBe(IpcSendResult.Accepted);
        await f.Ipc.WaitForDispatchesAsync();
        f.Shown.Last().Text.ShouldBe("synthetic OCR"); f.Shown.Last().AutoTranslate.ShouldBeTrue();
        f.Shown.Last().Origin.ShouldBe(TextOrigin.Screenshot);
        await f.Screenshot.Received(1).CaptureRegionAsync(ScreenshotPurpose.Translate, Arg.Any<CancellationToken>());
        f.Host.Services.GetRequiredService<IActionDispatcher>().ShouldBeOfType<ActionDispatcher>();
    });

    [Fact]
    public Task StopClosesWindowDependenciesBeforeWaitingForLastDispatchUiReturn() => Run(async f =>
    {
        await f.Bootstrap.StartAsync(); f.Ui.HoldNext = true;
        (await f.Send(new(IrisAction.TranslateText, "held"))).ShouldBe(IpcSendResult.Accepted);
        await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Bootstrap.StopAsync(); f.Tasks.Add(stop);
        f.Bootstrap.StopAsync().ShouldBeSameAs(stop);
        await f.OwnerClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.IsCompleted.ShouldBeFalse(); f.Exited.Task.IsCompleted.ShouldBeFalse();
        f.Ipc.TryAccept(new(IrisAction.Exit)).ShouldBe(IpcReply.Stopping);
        f.Ui.Release.TrySetResult(); await stop; f.Exited.Task.IsCompleted.ShouldBeTrue();
    });

    [Fact]
    public Task IpcExitUsesRegisteredHandlerAndCompletesWithoutWaitingOnItself() => Run(async f =>
    {
        await f.Bootstrap.StartAsync();
        var result = await f.Send(new(IrisAction.Exit));
        // Stop can cancel an acknowledgement already in flight; the client must not resend it.
        result.ShouldBeOneOf(IpcSendResult.Accepted, IpcSendResult.Unconfirmed);
        await f.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((AppLifetime)f.Host.Services.GetRequiredService<IAppLifetime>()).ShutdownWork!;
        f.HostStops.ShouldBe(1); f.Factory.Creates.ShouldBe(1);
    });

    [Fact]
    public Task StopDuringHostStartNeverReopensPipeOrRunsInitialCommand() => Run(async f =>
    {
        f.HoldHostStart = true;
        var start = f.Bootstrap.StartAsync(); f.Tasks.Add(start);
        await f.HostStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Bootstrap.StopAsync(); f.Tasks.Add(stop);
        stop.IsCompleted.ShouldBeFalse(); f.Factory.Creates.ShouldBe(0);
        f.ReleaseHostStart.TrySetResult(); await start; await stop;
        f.Factory.Creates.ShouldBe(0); f.Shown.ShouldBeEmpty();
        await f.Bootstrap.StartAsync(); f.Factory.Creates.ShouldBe(0);
    }, new(IrisAction.TranslateText, "never execute"));

    [Fact]
    public Task HostStopFailureRetainsOriginalPipeAndRetriesCleanupWithoutRedispatching() => Run(async f =>
    {
        await f.Bootstrap.StartAsync(); await f.Ipc.WaitForDispatchesAsync();
        f.Shown.Count.ShouldBe(1); f.Factory.FailDispose = true;
        await Should.ThrowAsync<Iris.Core.Errors.IrisException>(() => f.Bootstrap.StopAsync());
        f.HostStops.ShouldBe(0); f.Exited.Task.IsCompleted.ShouldBeFalse(); f.Factory.Disposals.ShouldBe(1);
        f.Factory.FailDispose = false; await f.Bootstrap.StopAsync();
        f.HostStops.ShouldBe(1); f.Factory.Creates.ShouldBe(1); f.Factory.Disposals.ShouldBe(2); f.Shown.Count.ShouldBe(1);
    }, new(IrisAction.TranslateText, "initial"));

    private static Task Run(Func<Scope, Task> body, IpcCommand? initial = null) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new Scope(); Exception? primary = null; var errors = new List<Exception>();
        var original = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = code => f.Exited.TrySetResult(code);
        try { await f.Initialize(initial); await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            f.Ui.Release.TrySetResult(); f.ReleaseHostStart.TrySetResult();
            f.Factory.FailDispose = false;
            try { await f.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            AppBootstrapper.AppShutdowner = original;
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
    private sealed class Scope : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        internal IHost Host = null!; internal AppBootstrapper Bootstrap => Host.Services.GetRequiredService<AppBootstrapper>();
        internal IpcCommandCoordinator Ipc => Host.Services.GetRequiredService<IpcCommandCoordinator>();
        internal readonly Factory Factory = new(); internal readonly Ui Ui = new();
        internal readonly ConcurrentQueue<TranslateWindowRequest> Shown = new();
        internal readonly IScreenshotService Screenshot = Substitute.For<IScreenshotService>();
        internal readonly TaskCompletionSource OwnerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<int> Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource HostStartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseHostStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<Task> Tasks = [];
        internal bool HoldHostStart, FailHostStart, HostAlreadyDisposed; internal int HostStops;
        internal async Task Initialize(IpcCommand? initial)
        {
            var paths = new FakeAppPaths(_directory.Path);
            await File.WriteAllTextAsync(paths.SettingsFile, JsonSerializer.Serialize(new AppSettings
            { Network = new() { ProxyMode = ProxyMode.None }, General = new() { ShowStartupNotification = false } }, IrisJson.Options));
            var presenter = Substitute.For<ITranslateWindowPresenter>(); var operation = Substitute.For<ITranslateWindowOperation>();
            presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
            presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(call =>
            { Shown.Enqueue(call.Arg<TranslateWindowRequest>()); return Task.FromResult(true); });
            var owner = Substitute.For<ITranslateWindowShutdownCoordinator>(); owner.ShutdownAsync().Returns(_ => { OwnerClosed.TrySetResult(); return Task.CompletedTask; });
            Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns(new ScreenshotResult(new([1, 2, 3], ImageFormat.Png, 4, 4), new(0, 0, 4, 4)));
            var ocr = Substitute.For<IOcrService>(); ocr.RecognizeAsync(Arg.Any<ImageData>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new OcrOutcome(new("fake", [], null, TimeSpan.Zero), "synthetic OCR", false));
            var hotkeys = Substitute.For<IHotkeyService>();
            hotkeys.Register(Arg.Any<IrisAction>(), Arg.Any<HotkeyBinding>()).Returns(call => new HotkeyRegistrationResult(call.Arg<IrisAction>(), true));
            Host = IsolatedAppHost.Build(["--translate", "initial private"], paths, new(), 0, services =>
            {
                services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(hotkeys);
                services.AddSingleton(Substitute.For<IHotkeyCaptureService>()); services.AddSingleton(Substitute.For<IAutoStartService>());
                services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
                services.AddSingleton(presenter); services.AddSingleton(owner); services.AddSingleton(Screenshot); services.AddSingleton(ocr);
                services.AddSingleton<IUiDispatcher>(Ui); services.AddSingleton<IpcPipeFactory>(Factory);
                services.AddSingleton<IHostedService>(new Hosted(this));
            }, initial);
            Host.Services.GetRequiredService<TrayIconController>().SuppressIconCreationForTesting = true;
        }
        internal async Task<IpcSendResult> Send(IpcCommand command)
        {
            await using var client = new NamedPipeIpcClient(Host.Services.GetRequiredService<IpcIdentity>(), new IpcPipeFactory(), TimeProvider.System);
            return await client.RunAsync(command);
        }
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            if (Host != null && !HostAlreadyDisposed)
            {
                try { await Bootstrap.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
                foreach (var task in Tasks) try { await task; } catch (Exception ex) { errors.Add(ex); }
                try { await ((IAsyncDisposable)Host).DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            }
            _directory.Dispose(); if (errors.Count != 0) throw new AggregateException(errors);
        }
        private sealed class Hosted(Scope owner) : IHostedService
        {
            public async Task StartAsync(CancellationToken cancellationToken)
            { owner.HostStartEntered.TrySetResult(); if (owner.HoldHostStart) await owner.ReleaseHostStart.Task; if (owner.FailHostStart) throw new IOException("synthetic-host-start"); }
            public Task StopAsync(CancellationToken cancellationToken) { owner.HostStops++; return Task.CompletedTask; }
        }
    }
    private sealed class Factory : IpcPipeFactory
    {
        internal int Creates, Disposals; internal bool FailDispose;
        internal override IpcPipe CreateServer(IpcIdentity identity) { Creates++; return new OwnedPipe(base.CreateServer(identity), this); }
        private sealed class OwnedPipe(IpcPipe inner, Factory owner) : IpcPipe((System.IO.Pipes.PipeStream)inner.Stream)
        {
            public override async ValueTask DisposeAsync()
            {
                owner.Disposals++; if (owner.FailDispose) throw new IOException("synthetic-pipe-disposal");
                await base.DisposeAsync();
            }
        }
    }
    private sealed class Ui : IUiDispatcher
    {
        private readonly WpfUiDispatcher _inner;
        internal Ui()
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _inner = new(() => dispatcher);
        }
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldNext;
        public bool CheckAccess() => _inner.CheckAccess();
        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            await _inner.InvokeAsync(callback, cancellationToken);
            if (HoldNext) { HoldNext = false; Entered.TrySetResult(); await Release.Task; }
        }
    }
}
