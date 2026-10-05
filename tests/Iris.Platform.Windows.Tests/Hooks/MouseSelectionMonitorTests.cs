using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Platform.Windows.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Tests.Hooks;

public sealed class MouseSelectionMonitorTests
{
    [Fact]
    public void ConstructionAndDiResolution_AreLazySingletonAndSafeToDispose()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddIrisPlatformWindows();
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IMouseSelectionMonitor>();
        monitor.ShouldBeOfType<MouseSelectionMonitor>();
        monitor.ShouldBeSameAs(provider.GetRequiredService<IMouseSelectionMonitor>());
        monitor.IsRunning.ShouldBeFalse(); monitor.Generation.ShouldBe(0);
        monitor.Dispose();
    }

    [Fact]
    public Task StartRestartAndStop_SeparateGenerationsAndRealThreadExit() => MouseTestRig.Use(async rig =>
    {
        rig.Api.Calls.ShouldBeEmpty();
        await rig.Start();
        long first = rig.Monitor.Generation;
        first.ShouldBe(1);
        var oldThread = rig.Api.MessageThread!;
        await rig.Start();
        oldThread.IsAlive.ShouldBeFalse();
        rig.Invalidations.Single().Generation.ShouldBe(first + 1);
        rig.Monitor.Generation.ShouldBe(first + 2);
        rig.Api.ActiveHandles.ShouldBe(1);
        await rig.Monitor.StopAsync();
        rig.Monitor.Generation.ShouldBe(first + 3);
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        await rig.Monitor.StopAsync();
        rig.Monitor.Generation.ShouldBe(first + 3);
        await rig.Start();
        rig.Monitor.Generation.ShouldBe(first + 4);
    });

    [Fact]
    public Task PreCancelledAndInvalidOptions_DoNotDisturbCurrentRun() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        long generation = rig.Monitor.Generation;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => rig.Monitor.StartAsync(MouseTestRig.Options(), cancelled.Token));
        foreach (var options in new MouseSelectionMonitorOptions[] { new(1, true, []), new(51, true, []), new(4, true, null!), new(4, true, [null!]), null! })
            Should.Throw<IrisException>(() => rig.Monitor.StartAsync(options)).Code.ShouldBe(ErrorCode.InvalidArgument);
        rig.Monitor.Generation.ShouldBe(generation); rig.Monitor.IsRunning.ShouldBeTrue();
        rig.Invalidations.ShouldBeEmpty(); rig.Api.ActiveHandles.ShouldBe(1);
    });

    [Fact]
    public Task Options_AreCopiedSynchronouslyAndExcludedNamesAreNotTrimmed() => MouseTestRig.Use(async rig =>
    {
        var excluded = new List<string> { "EDITOR.EXE" };
        rig.Api.InstallGate = rig.Gate();
        var start = rig.Start(new(4, false, excluded));
        excluded.Clear();
        await rig.Api.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Api.InstallGate.Release(); await start;
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 20); await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty();
        await rig.Start(new(4, false, [" editor.exe "]));
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 20); await rig.DrainAsync();
        rig.Gestures.Count.ShouldBe(1);
        rig.Gestures.Single().App!.WindowTitle.ShouldBeNull();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateInstall_StopOrCancelWaitsAndReclaimsHandle(bool cancel) => MouseTestRig.Use(async rig =>
    {
        using var cts = new CancellationTokenSource();
        rig.Api.InstallGate = rig.Gate();
        var start = rig.Start(token: cts.Token, expectedFailure: true);
        await rig.Api.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Should.Throw<IrisException>(() => rig.Monitor.StartAsync(MouseTestRig.Options())).Code.ShouldBe(ErrorCode.HookInstallFailed);
        Should.Throw<IrisException>(() => rig.Monitor.Dispose());
        if (cancel) cts.Cancel();
        var stop = rig.Monitor.StopAsync();
        rig.Monitor.StopAsync().ShouldBeSameAs(stop);
        start.IsCompleted.ShouldBeFalse(); stop.IsCompleted.ShouldBeFalse(); rig.Monitor.IsRunning.ShouldBeFalse();
        rig.Api.InstallGate.Release();
        await Should.ThrowAsync<OperationCanceledException>(() => start);
        await stop;
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        rig.Monitor.Generation.ShouldBe(2);
        await rig.Start();
    });

    [Fact]
    public Task CancelDuringRestart_DoesNotInstallNewHookOrReuseOldInvalidationGeneration() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        rig.Api.ExitGate = rig.Gate();
        using var cts = new CancellationTokenSource();
        var restart = rig.Start(token: cts.Token, expectedFailure: true);
        await rig.Api.ExitGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        restart.IsCompleted.ShouldBeFalse();
        rig.Api.ExitGate.Release();
        await Should.ThrowAsync<OperationCanceledException>(() => restart);
        rig.Api.Calls.Count(c => c.Name == nameof(rig.Api.SetHook)).ShouldBe(1);
        rig.Monitor.Generation.ShouldBe(2);
    });

    [Fact]
    public Task Stop_WaitsSubscriberNativeCallbackAndActualThreadReturn() => MouseTestRig.Use(async rig =>
    {
        var subscriber = rig.Gate();
        rig.Monitor.MouseDown += (_, _) => subscriber.Block();
        await rig.Start();
        await rig.Send(MouseMessage.LeftDown);
        await subscriber.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Api.NextGate = rig.Gate();
        var nativeCallback = rig.Send(MouseMessage.Move, 50);
        await rig.Api.NextGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Api.ExitGate = rig.Gate();
        var stop = rig.Monitor.StopAsync();
        var dispose = rig.Monitor.DisposeAsync().AsTask();
        stop.IsCompleted.ShouldBeFalse(); dispose.IsCompleted.ShouldBeFalse(); rig.Monitor.IsRunning.ShouldBeFalse();
        subscriber.Release(); rig.Api.NextGate.Release();
        await nativeCallback;
        await rig.Api.ExitGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.IsCompleted.ShouldBeFalse();
        rig.Api.ExitGate.Release(); await stop; await dispose;
        rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => rig.Monitor.StartAsync(MouseTestRig.Options()));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UnhookOrPostFailure_RetainsResponsibilityAndRetryDoesNotRepeatSuccess(bool unhook) => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        if (unhook) rig.Api.UnhookFailures = 1; else rig.Api.PostFailures = 1;
        var error = await Should.ThrowAsync<IrisException>(() => rig.Monitor.StopAsync());
        error.Code.ShouldBe(ErrorCode.HookInstallFailed); error.InnerException.ShouldBeNull();
        error.Detail!.ShouldContain(unhook ? "Unhook" : "PostQuit");
        rig.Monitor.IsRunning.ShouldBeFalse(); rig.Api.MessageThread!.IsAlive.ShouldBeTrue();
        rig.Api.ActiveHandles.ShouldBe(unhook ? 1 : 0);
        long generation = rig.Monitor.Generation;
        Should.Throw<IrisException>(() => rig.Monitor.StartAsync(MouseTestRig.Options()));
        Should.Throw<IrisException>(() => rig.Monitor.Dispose());
        await rig.Monitor.StopAsync();
        rig.Monitor.Generation.ShouldBe(generation); rig.Invalidations.Count.ShouldBe(1);
        rig.Api.Calls.Count(c => c.Name == nameof(rig.Api.Unhook)).ShouldBe(unhook ? 2 : 1);
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread.IsAlive.ShouldBeFalse();
        await rig.Start();
    });

    [Theory]
    [InlineData("PeekMessage")]
    [InlineData("LoadCursor")]
    [InlineData("SetHook")]
    public Task StartupFailures_AreSafeAndSameInstanceCanRetry(string stage) => MouseTestRig.Use(async rig =>
    {
        rig.Api.ThrowAt = stage;
        var error = await Should.ThrowAsync<IrisException>(() => rig.Start(expectedFailure: true));
        error.ToString().ShouldNotContain("PRIVATE_TITLE"); error.Detail!.ShouldNotContain("PRIVATE_TITLE"); error.InnerException.ShouldBeNull();
        rig.Api.ActiveHandles.ShouldBe(0);
        rig.Api.ThrowAt = null;
        await rig.Start();
    });

    [Fact]
    public Task InstallZero_IsFailureAndThreadReallyExits() => MouseTestRig.Use(async rig =>
    {
        rig.Api.InstallFailure = true;
        var error = await Should.ThrowAsync<IrisException>(() => rig.Start(expectedFailure: true));
        error.Detail!.ShouldContain("phase=Install;code=5");
        rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        rig.Api.InstallFailure = false; await rig.Start();
    });

    [Fact]
    public Task ThreadCreationFailure_DoesNotStrandConsumer() => MouseTestRig.Use(async rig =>
    {
        var error = await Should.ThrowAsync<IrisException>(() => rig.Start(expectedFailure: true));
        error.Detail!.ShouldContain("ThreadCreate");
        rig.Api.Calls.ShouldBeEmpty(); rig.Api.ActiveHandles.ShouldBe(0);
    }, new ThrowingThreadFactory());

    private sealed class ThrowingThreadFactory : IMouseThreadFactory
    {
        public Thread Create(ThreadStart action) => throw new InvalidOperationException("PRIVATE_TITLE");
    }

    [Fact]
    public Task RuntimePumpFailure_InvalidatesAndCleanupFailureCanBeRetried() => MouseTestRig.Use(async rig =>
    {
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Monitor.Invalidated += (_, _) => invalidated.TrySetResult();
        await rig.Start();
        rig.Api.UnhookFailures = 1;
        rig.Api.UnhookGate = rig.Gate();
        rig.Api.FailPump();
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Api.UnhookGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var failedStop = rig.Monitor.StopAsync();
        rig.Api.UnhookGate.Release();
        var error = await Should.ThrowAsync<IrisException>(() => failedStop);
        error.Detail.ShouldBe("phase=MessagePump;code=5;cleanup=Unhook;cleanupCode=5");
        await rig.Monitor.StopAsync();
        rig.Invalidations.Single().Error!.ShouldBe(new ErrorInfo(ErrorCode.HookInstallFailed, ErrorMessages.GetDefault(ErrorCode.HookInstallFailed)));
        rig.Api.ActiveHandles.ShouldBe(0); rig.Api.MessageThread!.IsAlive.ShouldBeFalse();
        await rig.Start();
    });

    [Fact]
    public Task NegativeCodeAndMetadataExceptions_AlwaysCallNextAndNeverThrowAcrossCallback() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        rig.Api.LastCallback!(-1, new WPARAM(0x201), new LPARAM(0)).Value.ShouldBe((nint)123);
        rig.Api.Calls.ShouldNotContain(c => c.Name == nameof(rig.Api.GetCursorInfo));
        rig.Api.ThrowAt = nameof(rig.Api.GetCursorInfo);
        await rig.Send(MouseMessage.LeftDown);
        rig.Api.NextCode.ShouldBe(0);
        rig.Api.ThrowAt = null;
        await rig.Send(MouseMessage.LeftUp, 30); await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty();
        rig.Api.Calls.Count(c => c.Name == nameof(rig.Api.CallNext)).ShouldBeGreaterThanOrEqualTo(4);
    });

    [Fact]
    public Task SubscriberFailure_DoesNotSkipOtherSubscribersOrStop() => MouseTestRig.Use(async rig =>
    {
        int reached = 0;
        rig.Monitor.MouseDown += (_, _) => throw new InvalidOperationException("PRIVATE_TITLE");
        rig.Monitor.MouseDown += (_, _) => reached++;
        rig.Monitor.Invalidated += (_, _) => throw new InvalidOperationException("PRIVATE_TITLE");
        await rig.Start(); await rig.Send(MouseMessage.LeftDown); await rig.DrainAsync();
        await rig.Monitor.StopAsync();
        reached.ShouldBe(2); rig.Api.ActiveHandles.ShouldBe(0);
    });

    [Fact]
    public Task StopRevokesEachSubscriberAndInvalidationRunsOutsideCallerAndLock() => MouseTestRig.Use(async rig =>
    {
        var subscriber = rig.Gate();
        int second = 0;
        rig.Monitor.MouseDown += (_, _) => subscriber.Block();
        rig.Monitor.MouseDown += (_, _) => second++;
        var invalidation = rig.Gate();
        rig.Monitor.Invalidated += (_, args) => { rig.Monitor.Generation.ShouldBe(args.Generation); invalidation.Block(); };
        await rig.Start(); await rig.Send(MouseMessage.LeftDown);
        await subscriber.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = rig.Monitor.StopAsync(); subscriber.Release();
        await invalidation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.IsCompleted.ShouldBeFalse();
        await Task.Run(() => rig.Monitor.Generation.ShouldBe(2)); // Can take state lock while subscriber is blocked.
        invalidation.Release(); await stop;
        second.ShouldBe(0);
    });

    [Fact]
    public Task Overflow_ResetsInFlightQueryAndDropsWholeLostEpoch() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        rig.Desktop.SourceGate = rig.Gate();
        await rig.Send(MouseMessage.LeftDown);
        await rig.Desktop.SourceGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 256; i++) await rig.Send(MouseMessage.Move, 20);
        await rig.Send(MouseMessage.LeftUp, 30); // queue is full; this up is lost
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Monitor.Invalidated += (_, _) => lost.TrySetResult();
        rig.Desktop.SourceGate.Release();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty(); rig.Invalidations.Count.ShouldBe(1);
        await rig.Send(MouseMessage.LeftUp, 30); await rig.DrainAsync(); rig.Gestures.ShouldBeEmpty();
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 30); await rig.DrainAsync();
        rig.Gestures.Single().Gesture.ShouldBe(SelectionGesture.Drag);
    });

    [Fact]
    public Task StaleAfterSourceQuery_AndOutOfOrderCannotCreateGesture() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Desktop.AfterSource = () => { rig.Api.Now = 351; queried.TrySetResult(); };
        await rig.Send(MouseMessage.LeftDown, time: 100);
        await queried.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Send(MouseMessage.LeftUp, 30, time: 100); await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty();
        rig.Desktop.AfterSource = null;
        rig.Api.Now = 400;
        await rig.Send(MouseMessage.LeftDown, time: 400); await rig.Send(MouseMessage.LeftUp, 30, time: 399); await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty();
        await rig.Start();
        rig.Api.Now = 5;
        await rig.Send(MouseMessage.LeftDown, time: uint.MaxValue - 3); await rig.Send(MouseMessage.LeftUp, 30, time: 5); await rig.DrainAsync();
        rig.Gestures.Single().Gesture.ShouldBe(SelectionGesture.Drag);
    });

    [Theory]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("path")]
    [InlineData("empty")]
    [InlineData("pid")]
    [InlineData("exception")]
    public Task UnconfirmedSource_RejectsCandidate(string scenario) => MouseTestRig.Use(async rig =>
    {
        switch (scenario)
        {
            case "self": rig.Api.SelfProcess = 42; break;
            case "missing": rig.Desktop.App = null; break;
            case "changed": rig.Desktop.App = rig.Desktop.App! with { WindowHandle = 1001 }; break;
            case "path": rig.Desktop.App = rig.Desktop.App! with { ProcessName = "C:\\editor.exe" }; break;
            case "empty": rig.Desktop.App = rig.Desktop.App! with { ProcessName = " " }; break;
            case "pid": rig.Api.PidSuccess = false; break;
            case "exception": rig.Desktop.ThrowSource = true; break;
        }
        await rig.Start(); await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 30); await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty();
    });

    [Fact]
    public Task InjectedAndNonLeftDown_StillPublishInputButNeverGesture() => MouseTestRig.Use(async rig =>
    {
        var inputs = new List<MouseSelectionInputEventArgs>();
        rig.Monitor.MouseDown += (_, args) => inputs.Add(args);
        await rig.Start();
        foreach (var message in new[] { MouseMessage.LeftDown, MouseMessage.RightDown, MouseMessage.MiddleDown, MouseMessage.XDown })
            await rig.Send(message, flags: 2);
        await rig.Send(MouseMessage.LeftUp, 30); await rig.DrainAsync(); await rig.Monitor.StopAsync();
        rig.Gestures.ShouldBeEmpty(); inputs.Count.ShouldBe(5); inputs.ShouldAllBe(args => args.Generation == 1);
    });

    [Fact]
    public void ContractToString_DoesNotRevealCoordinatesSourcesListsOrErrorDetails()
    {
        new SelectionGestureEventArgs(new(123456, -654321), SelectionGesture.Drag, new("PRIVATE", 42, "PRIVATE", 999)) { Generation = 8 }
            .ToString().ShouldBe("SelectionGestureEventArgs { Gesture=Drag, Generation=8 }");
        new MouseSelectionInputEventArgs(new(123456, -654321), 8).ToString().ShouldBe("MouseSelectionInputEventArgs { Generation=8 }");
        new MouseSelectionInvalidatedEventArgs(9, new(ErrorCode.HookInstallFailed, "PRIVATE", "PRIVATE"))
            .ToString().ShouldBe("MouseSelectionInvalidatedEventArgs { Generation=9, Failed=True }");
        new MouseSelectionMonitorOptions(4, true, ["PRIVATE"]).ToString().ShouldBe("MouseSelectionMonitorOptions");
    }
}
