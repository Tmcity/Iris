using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Selection;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class CompositeSelectionReaderTests
{
    [Fact]
    public Task ConstructorAndDi_AreLazyAndUseSameInstance() => SelectionTestScope.Run(async s =>
    {
        var services = new ServiceCollection();
        services.AddSingleton(s.Desktop);
        services.AddSingleton<IClipboardSnapshotNative>(s.Native);
        services.AddSingleton<ICopySafetyNative>(s.Focus);
        services.AddSingleton<INativeKeyboardInputAdapter>(s.Keys);
        services.AddSingleton<IUiaProcessRunner>(s.Runner);
        services.AddIrisPlatformWindows();
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISelectionTextReader>().ShouldBeSameAs(provider.GetRequiredService<CompositeSelectionReader>());
        s.Native.Calls.ShouldBeEmpty(); s.Keys.TotalNativeCalls.ShouldBe(0); s.Focus.Calls.ShouldBe(0);
    });

    [Fact]
    public Task EmptyOrder_HasNoPlatformEffects_AndOptionsAreValidated() => SelectionTestScope.Run(async s =>
    {
        (await s.Reader.ReadAsync(new([], true, 100, []), CancellationToken.None)).ShouldBeNull();
        s.Focus.Calls.ShouldBe(0); s.Keys.TotalNativeCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(null!, CancellationToken.None));
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(new([SelectionReadMethod.Clipboard], true, 99, []), CancellationToken.None));
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(new([(SelectionReadMethod)88], true, 100, []), CancellationToken.None));
    });

    [Fact]
    public Task ClipboardFirst_FreezesOptionsAndReturnsUnmodifiedText() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(); s.Gates.Add(release);
        s.OnSourceQuery = () => { entered.TrySetResult(); release.Wait(); };
        var order = new List<SelectionReadMethod> { SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard };
        var excluded = new List<string>();
        var task = s.Track(s.Reader.ReadAsync(new(order, true, 100, excluded), CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        order.Clear(); excluded.Add("editor.exe"); release.Set();
        await s.Tick(20);
        var result = (await task).ShouldNotBeNull();
        result.Method.ShouldBe(SelectionReadMethod.Clipboard);
        result.Text.ShouldBe(" selected text "); result.SourceApp.ShouldBe(s.Source);
        result.ToString().ShouldNotContain(result.Text); result.ToString().ShouldNotContain("private title");
        s.ClipboardText().ShouldBe("old text"); s.Copies.ShouldBe(1); s.Runner.TotalStartCalls.ShouldBe(0);
    });

    [Theory]
    [InlineData((int)UiaSelectionStatus.Sensitive, false)]
    [InlineData((int)UiaSelectionStatus.TargetChanged, false)]
    [InlineData((int)UiaSelectionStatus.TooLong, true)]
    public Task UiaSafetyOutcomes_DoNotFallback(int status, bool throws) => SelectionTestScope.Run(async s =>
    {
        s.UiaStatus = (UiaSelectionStatus)status;
        var task = s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard), CancellationToken.None);
        if (throws) (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.InputTooLong);
        else (await task).ShouldBeNull();
        s.Keys.TotalNativeCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
    });

    [Theory]
    [InlineData((int)UiaSelectionStatus.Empty)]
    [InlineData((int)UiaSelectionStatus.Unsupported)]
    [InlineData((int)UiaSelectionStatus.Failed)]
    [InlineData((int)UiaSelectionStatus.TimedOut)]
    public Task CleanUiaNoResult_FallsBackOnceInConfiguredOrder(int status) => SelectionTestScope.Run(async s =>
    {
        s.UiaStatus = (UiaSelectionStatus)status; s.Seed(); s.CopyOnSend("next");
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard), CancellationToken.None));
        await s.Tick(20);
        (await task)!.Text.ShouldBe("next"); s.Runner.TotalStartCalls.ShouldBe(1); s.Copies.ShouldBe(1);
    });

    [Theory]
    [InlineData("ConsoleWindowClass")]
    [InlineData("Chrome_RenderWidgetHostHWND")]
    [InlineData("EditExtra")]
    public Task ClipboardUnknownFocus_CannotAuthorizeCopy_ButMayUseUia(string name) => SelectionTestScope.Run(async s =>
    {
        s.Focus.ClassName = name; s.UiaStatus = UiaSelectionStatus.Success;
        var result = await s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation), CancellationToken.None);
        result!.Method.ShouldBe(SelectionReadMethod.UIAutomation);
        s.Keys.TotalNativeCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
    });

    [Fact]
    public Task ExcludedProcess_UsesCaseInsensitiveFileName_WithoutCopy() => SelectionTestScope.Run(async s =>
    {
        var result = await s.Reader.ReadAsync(new([SelectionReadMethod.Clipboard], true, 100, ["EDITOR.EXE"]), CancellationToken.None);
        result.ShouldBeNull(); s.Keys.TotalNativeCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
    });

    [Fact]
    public Task HeldUserKeys_DoNotPrepareOrClearClipboard() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.Keys.KeyStates[0xA2] = unchecked((short)0x8000);
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Time.WaitForDelayAsync(10);
        s.Native.Count("Open").ShouldBe(0); s.Copies.ShouldBe(0);
        s.Time.Advance(TimeSpan.FromMilliseconds(500));
        (await task).ShouldBeNull(); s.Native.Count("Empty").ShouldBe(0); s.ClipboardText().ShouldBe("old text");
    });

    [Theory]
    [InlineData("key")]
    [InlineData("focus")]
    [InlineData("terminal")]
    public Task ChangeDuringPreparation_RejectsSendAndRestores(string change) => SelectionTestScope.Run(async s =>
    {
        s.Seed();
        s.Native.After = api =>
        {
            if (api != "Empty") return;
            if (change == "key") s.Keys.KeyStates[0xA0] = unchecked((short)0x8000);
            if (change == "focus") s.Focus.Focus++;
            if (change == "terminal") s.Focus.ClassName = "ConsoleWindowClass";
        };
        (await s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None)).ShouldBeNull();
        s.Copies.ShouldBe(0); s.ClipboardText().ShouldBe("old text");
    });

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(999u, 12)]
    public Task UnknownClipboardOwner_IsNotReadOrOverwritten(uint pid, int owner) => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("other", pid, owner);
        (await s.Reader.ReadAsync(SelectionTestScope.Options(false), CancellationToken.None)).ShouldBeNull();
        s.Native.Count("Get").ShouldBe(0); s.ClipboardText().ShouldBe("other");
    });

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public Task NoNewCopy_DoesNotAcceptSelfClearOrOldText(uint sequence) => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.Native.Version = new(sequence, 0, 0);
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(false), CancellationToken.None));
        await s.Tick(100, 15);
        (await task).ShouldBeNull(); s.Native.Count("Get").ShouldBe(0); s.Native.Formats.ShouldBeEmpty();
    });

    [Fact]
    public Task ChangedDuringStableWindow_PreservesExternalContent() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("candidate");
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Time.WaitForDelayAsync(20);
        s.ExternalCopy("external", 999, 77); s.Time.Advance(TimeSpan.FromMilliseconds(20));
        await s.Tick(15);
        (await task).ShouldBeNull(); s.ClipboardText().ShouldBe("external");
    });

    [Fact]
    public Task NativeTextReadPastBudget_DoesNotPublishLateText() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("late");
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(false), CancellationToken.None));
        await s.Time.WaitForDelayAsync(20);
        s.Native.Before = api => { if (api == "Get") s.Time.Advance(TimeSpan.FromMilliseconds(100)); };
        s.Time.Advance(TimeSpan.FromMilliseconds(20));
        (await task).ShouldBeNull();
    });

    [Fact]
    public Task CancelAfterClear_RestoresBeforeC1Stops() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); using var cts = new CancellationTokenSource();
        s.Native.After = api => { if (api == "Empty") cts.Cancel(); };
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), cts.Token), typeof(OperationCanceledException));
        await Should.ThrowAsync<OperationCanceledException>(() => task);
        s.ClipboardText().ShouldBe("old text"); s.Copies.ShouldBe(0);
    });

    [Fact]
    public Task RestoreFailure_IsRetainedAndRetryDoesNotCopyOrPublish() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("candidate");
        var task = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Time.WaitForDelayAsync(20); s.Native.Failures["Set"] = -1; s.Time.Advance(TimeSpan.FromMilliseconds(20));
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.CaptureFailed);
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        s.Copies.ShouldBe(1);
        s.Native.Failures.Clear(); await s.Reader.RetryCleanupAsync();
        s.ClipboardText().ShouldBe("old text"); s.Copies.ShouldBe(1);
        s.UiaStatus = UiaSelectionStatus.Success;
        (await s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation), CancellationToken.None))!.Text.ShouldBe("uia text");
    });

    [Fact]
    public Task Stop_WaitsForRestoreBeforeDestroyingC1Owner() => SelectionTestScope.Run(s => RestoreStopScenario(s, null));

    private static async Task RestoreStopScenario(SelectionTestScope s, Exception? injected)
    {
        s.Seed();
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(15);
        var gate = s.NativeGate("Set");
        var stop = s.Track(s.Reader.StopAsync());
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        stop.IsCompleted.ShouldBeFalse(); s.Native.WindowAlive.ShouldBeTrue(); s.Reader.StopAsync().ShouldBeSameAs(stop);
        if (injected != null) throw injected;
        gate.Release.Set(); await stop;
        s.ClipboardText().ShouldBe("old text"); s.Native.WindowAlive.ShouldBeFalse(); read.IsCompleted.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDuringActualRestore_ReleasesGateAndRetainsErrors(bool cleanupFails)
    {
        var scope = new SelectionTestScope();
        var body = new InvalidOperationException("injected body");
        var cleanup = new IOException("injected cleanup");
        if (cleanupFails) scope.AfterCleanup = () => throw cleanup;
        if (cleanupFails)
        {
            var error = await Should.ThrowAsync<AggregateException>(() => SelectionTestScope.Run(s => RestoreStopScenario(s, body), scope));
            error.InnerExceptions.ShouldContain(body); error.InnerExceptions.ShouldContain(cleanup);
        }
        else (await Should.ThrowAsync<InvalidOperationException>(() => SelectionTestScope.Run(s => RestoreStopScenario(s, body), scope))).ShouldBeSameAs(body);
        scope.Joined.ShouldBeTrue(); scope.ClipboardText().ShouldBe("old text"); scope.Tasks.ShouldAllBe(t => t.Task.IsCompleted);
    }

    [Fact]
    public Task BusyRequest_DoesNotBecomeOrdinaryNoResult() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.Keys.KeyStates[0xA2] = unchecked((short)0x8000);
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(OperationCanceledException));
        await s.Time.WaitForDelayAsync(10);
        var error = await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        error.Message.ShouldContain("SELECTION_BUSY"); await s.Reader.StopAsync(); await Should.ThrowAsync<OperationCanceledException>(() => read);
    });

    [Fact]
    public Task ClearCloseFailure_RetryUsesReceiptAndNeverSendsCopy() => SelectionTestScope.Run(async s =>
    {
        s.Seed();
        s.Native.After = api => { if (api == "Empty") s.Native.Failures["Close"] = -1; };
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        s.Copies.ShouldBe(0);
        s.Native.After = null; s.Native.Failures.Clear();
        await s.Reader.RetryCleanupAsync(); s.ClipboardText().ShouldBe("old text"); s.Copies.ShouldBe(0);
    });

    [Fact]
    public Task RestoreCloseFailure_RetryDoesNotRepeatSuccessfulRestore() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend();
        s.Native.After = api => { if (api == "Set") s.Native.Failures["Close"] = -1; };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Tick(20); await Should.ThrowAsync<IrisException>(() => read);
        int emptied = s.Native.Count("Empty"); int published = s.Native.Count("Set");
        s.Native.After = null; s.Native.Failures.Clear(); await s.Reader.RetryCleanupAsync();
        s.Native.Count("Empty").ShouldBe(emptied); s.Native.Count("Set").ShouldBe(published);
        s.ClipboardText().ShouldBe("old text"); s.Copies.ShouldBe(1);
    });

    [Fact]
    public Task KeyboardCleanupFailure_IsRetainedAndRetryOnlyReleasesOwnedKeys() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); bool fail = true; s.Release.Add(() => fail = false);
        s.Keys.CustomSendInputHandler = (_, inputs, _) =>
        {
            var key = inputs[0].Anonymous.ki;
            if (key.dwFlags != 0) return fail ? 0u : 1u;
            if ((int)key.wVk == 0x43) s.ExternalCopy("may have copied");
            return 1;
        };
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        s.Keyboard.HasPendingRelease.ShouldBeTrue(); s.Copies.ShouldBe(1);
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        fail = false; await s.Reader.RetryCleanupAsync();
        s.Keyboard.HasPendingRelease.ShouldBeFalse(); s.Copies.ShouldBe(1);
        s.UiaStatus = UiaSelectionStatus.Success;
        (await s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation), CancellationToken.None))!.Text.ShouldBe("uia text");
    });

    [Fact]
    public Task UiaCleanupFailure_BlocksFallbackAndRetainsSameReaderForRetry() => SelectionTestScope.Run(async s =>
    {
        var normalStart = s.Runner.StartAsyncFunc;
        var owner = new FakeUiaProcessSession(); owner.SignalExit();
        owner.OnDispose = () => throw new IOException("private uia cleanup");
        s.Release.Add(() => owner.OnDispose = null);
        s.Runner.StartAsyncFunc = _ => throw new ProcessStartupCleanupException(owner, "private start error");
        var error = await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard), CancellationToken.None));
        error.ToString().ShouldNotContain("private"); s.Copies.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
        owner.OnDispose = null; await s.Reader.RetryCleanupAsync();
        s.Runner.StartAsyncFunc = normalStart; s.UiaStatus = UiaSelectionStatus.Success;
        (await s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation), CancellationToken.None))!.Method.ShouldBe(SelectionReadMethod.UIAutomation);
    });

    [Theory]
    [InlineData((int)UiaSelectionStatus.Success)]
    [InlineData((int)UiaSelectionStatus.Unsupported)]
    public Task SourceChangesAfterUiaStart_DoesNotPublishOrRetargetClipboard(int status) => SelectionTestScope.Run(async s =>
    {
        var start = s.Runner.StartAsyncFunc!; s.UiaStatus = (UiaSelectionStatus)status;
        s.Runner.StartAsyncFunc = token => { s.Source = s.Source! with { WindowHandle = 999 }; return start(token); };
        (await s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard), CancellationToken.None)).ShouldBeNull();
        s.Copies.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
    });

    [Fact]
    public Task CandidateReadExternalChange_DoesNotOverwriteNewClipboard() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("candidate");
        s.Native.After = api => { if (api == "CopyFrom" && s.Native.Version.OwnerProcessId == 5000) s.ExternalCopy("external", 999, 77); };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Tick(20); (await read).ShouldBeNull(); s.ClipboardText().ShouldBe("external");
    });

    [Fact]
    public Task CandidateTooLong_ThrowsAfterRestoringSnapshot() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend(new string('x', 65_537));
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Tick(20);
        (await Should.ThrowAsync<IrisException>(() => read)).Code.ShouldBe(ErrorCode.InputTooLong);
        s.ClipboardText().ShouldBe("old text");
    });

    [Fact]
    public Task CopySequenceWrap_IsAcceptedByEqualityAndRestored() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.Native.Version = new(uint.MaxValue - 1, 0, 0); s.CopyOnSend("wrapped");
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Tick(20); (await read)!.Text.ShouldBe("wrapped"); s.ClipboardText().ShouldBe("old text");
    });

    [Fact]
    public Task SourceChangeDuringRestore_DropsOtherwiseSuccessfulResult() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend();
        s.Native.After = api => { if (api == "Set") s.Source = s.Source! with { WindowHandle = 77 }; };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Tick(20); (await read).ShouldBeNull(); s.ClipboardText().ShouldBe("old text");
    });
}
