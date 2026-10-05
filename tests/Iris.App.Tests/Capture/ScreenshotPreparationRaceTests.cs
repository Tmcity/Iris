using Iris.App.Capture;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.App.Tests.Capture;

public sealed class ScreenshotPreparationRaceTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task QueuedShow_RevokesLeaseAtActualUiAcceptance(bool settings) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        f.Ui.HoldNext = 1;
        Task show = settings ? f.Settings.ShowAsync() : f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        f.Work.Add(show);
        await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var lease = await f.Preparation.PrepareAsync(true);
        lease.Reason.ShouldBe(PreparationRevocation.None);
        f.Ui.Release.TrySetResult(); await show;
        lease.Reason.ShouldBe(PreparationRevocation.DisplayIntent);
        await lease.DisposeAsync();
        if (settings) f.Settings.ActiveSession!.Window.IsVisible.ShouldBeTrue();
        else f.Mouse.Presenter.Window!.IsVisible.ShouldBeTrue();
    });

    [Theory]
    [InlineData("operation")][InlineData("cancel")][InlineData("stop")]
    public Task PreparationUiQueued_NewIntentOrCancellationWaitsActualQueue(string intent) => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings();
        using var caller = new CancellationTokenSource();
        f.Ui.HoldNext = 1;
        var prepare = f.Preparation.PrepareAsync(true, caller.Token); f.Work.Add(prepare);
        await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task? stop = null;
        if (intent == "operation") { using var operation = f.Mouse.Presenter.TryBeginOperation(); }
        else if (intent == "cancel") caller.Cancel();
        else { stop = f.Preparation.StopAsync(); f.Work.Add(stop); }
        prepare.IsCompleted.ShouldBeFalse(); stop?.IsCompleted.ShouldBeFalse();
        f.Ui.Release.TrySetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => prepare);
        if (stop != null) await stop;
        f.Settings.ActiveSession!.Window.IsVisible.ShouldBeTrue();
    });

    [Fact]
    public Task ReturnQueued_StopSkipsRestorationAtUiAcceptance() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var window = f.Settings.ActiveSession!.Window;
        var lease = await f.Preparation.PrepareAsync(true);
        f.Ui.HoldNext = 1;
        var returning = lease.DisposeAsync().AsTask(); f.Work.Add(returning);
        await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = f.Preparation.StopAsync(); f.Work.Add(stop);
        stop.IsCompleted.ShouldBeFalse();
        f.Ui.Release.TrySetResult(); await Task.WhenAll(stop, returning);
        window.IsVisible.ShouldBeFalse(); f.Recording.CanStartRecording.ShouldBeFalse();
    });

    [Fact]
    public Task ReturnQueued_NewSessionCannotBeRestoredAsOldSession() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var old = f.Settings.ActiveSession!;
        var lease = await f.Preparation.PrepareAsync(true);
        f.Ui.HoldNext = 1;
        var returning = lease.DisposeAsync().AsTask(); f.Work.Add(returning);
        await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await old.ShutdownAsync(); await f.OpenSettings();
        var current = f.Settings.ActiveSession!; current.ShouldNotBeSameAs(old); current.Window.Hide();
        f.Ui.Release.TrySetResult(); await returning;
        old.IsOpen.ShouldBeFalse(); current.Window.IsVisible.ShouldBeFalse();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task FailedRestore_NewIntentExpiresItOrNextPreparationRetriesIt(bool newIntent) => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings();
        var visibility = new ScreenshotPreparationTests.Visibility { FailShow = true };
        f.Preparation.Visibility = visibility;
        var lease = await f.Preparation.PrepareAsync(true);
        await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
        if (newIntent)
        {
            await f.Settings.ShowAsync(); f.Settings.ActiveSession!.Window.Hide();
            await f.Preparation.RetryCleanupAsync();
            f.Settings.ActiveSession.Window.IsVisible.ShouldBeFalse();
        }
        else
        {
            visibility.FailShow = false;
            var next = await f.Preparation.PrepareAsync(true);
            f.Settings.ActiveSession!.Window.IsVisible.ShouldBeFalse();
            await next.DisposeAsync(); f.Settings.ActiveSession.Window.IsVisible.ShouldBeTrue();
        }
    });

    [Fact]
    public Task ReturnWaitsRevocationCallback_AndPreservesFirstReason() => PreparationTestScope.Run(async f =>
    {
        using var caller = new CancellationTokenSource();
        var lease = await f.Preparation.PrepareAsync(false, caller.Token);
        var gate = f.Mouse.Gate();
        var registration = lease.Revoked.Register(gate.Block);
        try
        {
            caller.Cancel();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = f.Preparation.StopAsync(); f.Work.Add(stop);
            stop.IsCompleted.ShouldBeFalse(); lease.Reason.ShouldBe(PreparationRevocation.CallerCanceled);
            gate.Release(); await stop;
            lease.Reason.ShouldBe(PreparationRevocation.CallerCanceled);
        }
        finally { gate.Release(); await registration.DisposeAsync(); }

    });

    [Fact]
    public Task FailedIconCleanup_BlocksPreparation_RetryRetainsRuntimeError() => PreparationTestScope.Run(async f =>
    {
        await f.Mouse.Mode(MouseSelectionMode.ShowIcon); await f.Mouse.Controller.StartAsync(); await f.Mouse.ShowCandidate();
        f.Mouse.Icon.FailHide = true;
        var failure = await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true));
        failure.Code.ShouldBe(ErrorCode.CaptureFailed); failure.Detail!.ShouldContain("MOUSE");
        f.Mouse.Controller.Runtime.CanRetry.ShouldBeTrue();
        f.Mouse.Icon.FailHide = false;
        await f.Preparation.RetryCleanupAsync();
        f.Mouse.Factory.Windows.Single().IsVisible.ShouldBeFalse();
        f.Mouse.Controller.Runtime.CanRetry.ShouldBeTrue();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task PauseRelease_DoesNotUndoModeOffOrStop(bool stop) => PreparationTestScope.Run(async f =>
    {
        await f.Mouse.Mode(MouseSelectionMode.ShowIcon); await f.Mouse.Controller.StartAsync(); await f.Mouse.ShowCandidate();
        long generation = f.Mouse.Monitor.Generation;
        var lease = await f.Preparation.PrepareAsync(false);
        f.Mouse.Monitor.Generation.ShouldBe(generation);
        if (stop) await f.Mouse.Controller.StopAsync();
        else { await f.Mouse.Mode(MouseSelectionMode.Off); await f.Mouse.Controller.WaitForApplyAsync(); }
        await lease.DisposeAsync();
        f.Mouse.Monitor.IsRunning.ShouldBeFalse(); f.Mouse.Mouse.ActiveHandles.ShouldBe(0);
        f.Mouse.Factory.Windows.Single().IsVisible.ShouldBeFalse();
    });

    [Theory]
    [InlineData(MouseSelectionMode.ShowIcon)][InlineData(MouseSelectionMode.AutoTranslate)]
    public Task ActualClipboardRestoreGate_PreparationWaitsOriginalBytesAndThread(MouseSelectionMode mode) => PreparationTestScope.Run(async f =>
    {
        var p = f.Mouse.Platform; p.Seed();
        await f.Mouse.Settings.UpdateAsync(s => s with { Selection = s.Selection with
            { MouseMode = mode, ReadOrder = [SelectionReadMethod.Clipboard] } });
        await f.Mouse.Controller.StartAsync(); await f.Mouse.Drag(); p.Time.Advance(TimeSpan.FromMilliseconds(120));
        await p.Time.WaitForDelayAsync(15);
        var restoring = p.NativeGate("Set");
        var prepare = f.Preparation.PrepareAsync(false); f.Work.Add(prepare);
        await restoring.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        prepare.IsCompleted.ShouldBeFalse(); p.Copies.ShouldBe(1);
        restoring.Release.Set();
        var lease = await prepare; await lease.DisposeAsync();
        p.Copies.ShouldBe(1); p.ClipboardText().ShouldBe("old text"); f.Mouse.Factory.Windows.ShouldBeEmpty();
        await f.Mouse.Reader.StopAsync(); await p.Reader.StopAsync();
        p.Native.WindowAlive.ShouldBeFalse();
    });

    [Fact]
    public Task ActiveStreamingOperation_SurvivesTemporaryHideAndReturn() => PreparationTestScope.Run(async f =>
    {
        f.Mouse.Provider.HoldAll = true;
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { Text = "synthetic stream", AutoTranslate = true, FocusInput = false });
        await f.Mouse.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var lease = await f.Preparation.PrepareAsync(true);
        f.Mouse.Presenter.Window!.IsVisible.ShouldBeFalse();
        operation.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        f.Mouse.Provider.Cancellations.ShouldBe(0);
        await lease.DisposeAsync();
        f.Mouse.Provider.Cancellations.ShouldBe(0); f.Mouse.Presenter.Window.IsVisible.ShouldBeTrue();
        f.Mouse.Provider.Release.TrySetResult();
        await f.Mouse.Presenter.ViewModel!.WaitForActiveWorkAsync();
    });

    [Fact]
    public Task StalePublication_DoesNotRevokeNewPreparation() => PreparationTestScope.Run(async f =>
    {
        using var stale = f.Mouse.Presenter.TryBeginOperation()!;
        using var current = f.Mouse.Presenter.TryBeginOperation()!;
        var lease = await f.Preparation.PrepareAsync(true);
        (await f.Mouse.Presenter.TryShowAsync(stale, new())).ShouldBeFalse();
        lease.Reason.ShouldBe(PreparationRevocation.None);
        await lease.DisposeAsync();
    });

    [Fact]
    public Task RecordingFailure_RemainsRetryable_AndNeverCommitsCapturedBinding() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var vm = f.Settings.ActiveSession!.ViewModel; var row = vm.Hotkeys[0];
        var original = row.TargetBinding;
        f.Native.FailUnhook = true;
        var record = vm.StartRecordingAsync(row); f.Work.Add(record);
        await f.Native.Inner.PumpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Synthetic metadata passed only to the fake hook callback.
        await f.Native.Inner.KeyAsync(0xA2); await f.Native.Inner.KeyAsync(0xA4); await f.Native.Inner.KeyAsync(0x41);
        await f.Native.Inner.KeyAsync(0x41, true); await f.Native.Inner.KeyAsync(0xA4, true); await f.Native.Inner.KeyAsync(0xA2, true);
        await record;
        vm.HasFailedRecordingCleanup.ShouldBeTrue(); row.TargetBinding.ShouldBe(original);
        await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true));
        f.Settings.ActiveSession.Window.IsVisible.ShouldBeTrue();
        vm.HasFailedRecordingCleanup.ShouldBeTrue(); vm.CanRetryHotkeysOrCleanup.ShouldBeTrue();
        row.IsRecording.ShouldBeFalse(); row.TargetBinding.ShouldBe(original);
        f.Native.FailUnhook = false;
        await f.Preparation.RetryCleanupAsync();
        vm.HasFailedRecordingCleanup.ShouldBeFalse(); vm.IsAnyRecording.ShouldBeFalse(); vm.CanEdit.ShouldBeTrue();
        row.TargetBinding.ShouldBe(original);
        var next = vm.StartRecordingAsync(row); f.Work.Add(next);
        await f.Native.SecondInstalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Request cancellation waits the actual next session even if Begin has not returned yet.
        await vm.CancelRecordingAsync(); await next;
        f.Native.Installs.ShouldBe(2);
    });

    [Fact]
    public Task MonitorFaultWhilePaused_IsNotClearedByReturn() => PreparationTestScope.Run(async f =>
    {
        await f.Mouse.Mode(MouseSelectionMode.ShowIcon); await f.Mouse.Controller.StartAsync();
        var lease = await f.Preparation.PrepareAsync(false);
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Mouse.Monitor.Invalidated += (_, _) => invalidated.TrySetResult();
        f.Mouse.Mouse.FailPump(); await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await lease.DisposeAsync();
        f.Mouse.Controller.Runtime.Error.ShouldBe(ErrorCode.HookInstallFailed);
        f.Mouse.Controller.Runtime.CanRetry.ShouldBeTrue(); f.Mouse.Monitor.IsRunning.ShouldBeFalse();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task RestoreNativeReentrancy_DoesNotPublishOldVisibility(bool shutdown) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var visibility = new ScreenshotPreparationTests.Visibility(); f.Preparation.Visibility = visibility;
        var lease = await f.Preparation.PrepareAsync(true);
        visibility.BeforeShow = () =>
        {
            if (shutdown) f.Preparation.StopAcceptance();
            else { using var next = f.Mouse.Presenter.TryBeginOperation(); }
        };
        await lease.DisposeAsync();
        f.Mouse.Presenter.Window!.IsVisible.ShouldBeFalse();
        lease.Reason.ShouldBe(shutdown ? PreparationRevocation.Shutdown : PreparationRevocation.DisplayIntent);
    });

    [Fact]
    public Task MultipleRestoreFailures_RetryOnlyRemainingStages() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var visibility = new ScreenshotPreparationTests.Visibility { FailShow = true }; f.Preparation.Visibility = visibility;
        var lease = await f.Preparation.PrepareAsync(true);
        var error = await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
        error.Detail!.ShouldContain("SETTINGS"); error.Detail!.ShouldContain("TRANSLATION");
        f.Recording.CanStartRecording.ShouldBeTrue();
        visibility.FailShow = false; await f.Preparation.RetryCleanupAsync();
        f.Settings.ActiveSession!.Window.IsVisible.ShouldBeTrue(); f.Mouse.Presenter.Window!.IsVisible.ShouldBeTrue();
    });

    [Fact]
    public Task CancellationCallbackFailure_IsSafeRetainedAndRetryable() => PreparationTestScope.Run(async f =>
    {
        using var caller = new CancellationTokenSource();
        var lease = await f.Preparation.PrepareAsync(false, caller.Token);
        using var registration = lease.Revoked.Register(() => throw new InvalidOperationException("PRIVATE_CALLBACK"));
        caller.Cancel();
        var error = await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
        error.Detail!.ShouldContain("CANCEL"); error.ToString().ShouldNotContain("PRIVATE_CALLBACK");
        await f.Preparation.RetryCleanupAsync();
    });

    [Fact]
    public Task LateRestoreUndoFailure_RetainsOnlyItsOldVisibilityForRetry() => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var visibility = new ScreenshotPreparationTests.Visibility(); f.Preparation.Visibility = visibility;
        var lease = await f.Preparation.PrepareAsync(true);
        visibility.BeforeShow = () =>
        {
            using var next = f.Mouse.Presenter.TryBeginOperation();
            visibility.FailBeforeHide = true;
        };
        var error = await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
        error.Detail!.ShouldContain("LATE_HIDE"); f.Mouse.Presenter.Window!.IsVisible.ShouldBeTrue();
        visibility.FailBeforeHide = false;
        await f.Preparation.RetryCleanupAsync(); f.Mouse.Presenter.Window.IsVisible.ShouldBeFalse();
    });

    [Fact]
    public Task RepeatedFailedPreflight_RemainsBoundedAndLaterRestoresOriginal() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var session = f.Settings.ActiveSession!;
        var visibility = new ScreenshotPreparationTests.Visibility { FailShow = true }; f.Preparation.Visibility = visibility;
        var lease = await f.Preparation.PrepareAsync(true);
        await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
        for (int i = 0; i < 4; i++)
            await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true));
        visibility.FailShow = false;
        var next = await f.Preparation.PrepareAsync(true); await next.DisposeAsync();
        f.Settings.ActiveSession.ShouldBeSameAs(session); session.Window.IsVisible.ShouldBeTrue();
    });
}
