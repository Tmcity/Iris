using System.Runtime.ExceptionServices;
using Iris.Core.Errors;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class ClipboardSnapshotServiceTests
{
    [Fact]
    public async Task Constructor_AndDiResolution_AreLazy()
    {
        var native = new FakeClipboardSnapshotNative();
        var services = new ServiceCollection();
        services.AddSingleton<IClipboardSnapshotNative>(native);
        services.AddIrisPlatformWindows();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ClipboardSnapshotService>();
        service.ShouldBeSameAs(provider.GetRequiredService<ClipboardSnapshotService>());
        native.Calls.ShouldBeEmpty();
        await service.StopAsync(TestContext.Current.CancellationToken);
        native.Calls.ShouldBeEmpty();
    }

    [Fact]
    public Task CaptureRestore_PreservesRawBytesOrderAndOwnerVersion() => Run(async s =>
    {
        var formats = new uint[] { 13, 1, 7, 16, 15, 8, 17, 0xC001, 0xC002, 0xC003 };
        s.Native.Names[0xC001] = "HTML Format";
        s.Native.Names[0xC002] = "Rich Text Format";
        s.Native.Names[0xC003] = "PNG";
        byte[] raw = [0, 255, 12, 0, 33, 128];
        foreach (uint f in formats) s.Native.Add(f, raw);
        var snapshot = await s.Capture();
        snapshot.ToString().ShouldBe("ClipboardSnapshot Formats=10");
        snapshot.Version.ToString().ShouldBe("ClipboardVersion");
        foreach (var block in s.Native.Memory.Values) block.Bytes[0] = 99;
        var result = await s.Service.TryRestoreAsync(snapshot, snapshot.Version);
        result.Status.ShouldBe(ClipboardSnapshotStatus.Applied);
        result.Version.Owner.ShouldBe(900);
        result.Version.OwnerProcessId.ShouldBe(42u);
        s.Native.Formats.Select(f => f.Format).ShouldBe(formats);
        foreach (var item in s.Native.Formats) s.Native.Memory[item.Handle].Bytes.ShouldBe(raw);
        s.Native.Count("Free").ShouldBe(0);
        s.Native.Calls.Select(c => c.Thread).Distinct().Count().ShouldBe(1);
        s.Native.Calls.ShouldAllBe(c => c.Apartment == ApartmentState.MTA);
        s.Native.Count("Pump").ShouldBeGreaterThan(0);
    });

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public Task EmptySnapshot_AndSequenceWrap_AreLegal(uint sequence) => Run(async s =>
    {
        s.Native.Version = new(sequence, 0, 0);
        var snapshot = await s.Capture();
        snapshot.FormatCount.ShouldBe(0);
        var cleared = await s.Service.TryRestoreAsync(snapshot, snapshot.Version);
        cleared.Status.ShouldBe(ClipboardSnapshotStatus.Applied);
        cleared.Version.Sequence.ShouldBe(unchecked(sequence + 1));
        s.Native.Count("Set").ShouldBe(0);
        (await s.Service.ReadVersionAsync()).ShouldBe(cleared.Version);
    });

    [Theory]
    [InlineData(2u)]
    [InlineData(9u)]
    [InlineData(0x80u)]
    [InlineData(0x200u)]
    [InlineData(0x300u)]
    [InlineData(0xC000u)]
    public Task Capture_UnsupportedFormat_IsRejectedBeforeAnyPayload(uint format) => Run(async s =>
    {
        s.Native.Add(13, [1, 2]);
        s.Native.Add(format, [3]);
        var result = await s.Service.CaptureSnapshotAsync();
        result.Status.ShouldBe(ClipboardSnapshotStatus.UnsupportedFormat);
        result.Snapshot.ShouldBeNull();
        s.Native.Count("Get").ShouldBe(0);
        s.Native.Count("Empty").ShouldBe(0);
    });

    [Theory]
    [InlineData(64, (int)ClipboardSnapshotStatus.Success)]
    [InlineData(65, (int)ClipboardSnapshotStatus.TooLarge)]
    public Task Capture_FormatCount_IsBounded(int count, int expected) => Run(async s =>
    {
        for (uint i = 0; i < count; i++) { s.Native.Names[0xC000 + i] = "PNG"; s.Native.Add(0xC000 + i, [1]); }
        var result = await s.Service.CaptureSnapshotAsync();
        if (result.Snapshot != null) s.Snapshots.Add(result.Snapshot);
        result.Status.ShouldBe((ClipboardSnapshotStatus)expected);
        if (count > 64) { s.Native.Count("Get").ShouldBe(0); s.Native.Count("Enum").ShouldBe(65); }
    });

    [Theory]
    [InlineData(33_554_433L, 1, (int)ClipboardSnapshotStatus.TooLarge)]
    [InlineData(33_554_432L, 2, (int)ClipboardSnapshotStatus.Success)]
    [InlineData(33_554_432L, 3, (int)ClipboardSnapshotStatus.TooLarge)]
    public Task Capture_ByteBudgets_AreCheckedBeforeCopy(long bytes, int count, int expected) => Run(async s =>
    {
        uint[] formats = [1, 7, 13];
        for (int i = 0; i < count; i++) s.Native.Add(formats[i], [1], (nuint)(i == 2 ? 1 : bytes));
        var result = await s.Service.CaptureSnapshotAsync();
        if (result.Snapshot != null) s.Snapshots.Add(result.Snapshot);
        result.Status.ShouldBe((ClipboardSnapshotStatus)expected);
        s.Native.Count("CopyFrom").ShouldBe(bytes > ClipboardSnapshotService.MaxFormatBytes ? 0 : 2);
        s.Native.Count("Free").ShouldBe(0);
    });

    [Theory]
    [InlineData("sequence")]
    [InlineData("owner")]
    [InlineData("formats")]
    public Task Capture_DelayedRenderingChange_DiscardsWholeSnapshot(string mutation) => Run(async s =>
    {
        s.Native.Add(13, [1, 2]);
        s.Native.After = api =>
        {
            if (api != "CopyFrom") return;
            if (mutation == "sequence") s.Native.Version = s.Native.Version with { Sequence = 1 };
            if (mutation == "owner") s.Native.Version = new(0, 18, 19);
            if (mutation == "formats") s.Native.Add(1, [1]);
        };
        var result = await s.Service.CaptureSnapshotAsync();
        result.Status.ShouldBe(ClipboardSnapshotStatus.Changed);
        result.Snapshot.ShouldBeNull();
        s.Native.Count("Empty").ShouldBe(0);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConditionalWrite_ChangeAfterOpen_DoesNotEmptyOrSet(bool restore) => Run(async s =>
    {
        s.Native.Add(13, [1, 2]);
        var snapshot = await s.Capture();
        s.Native.After = api => { if (api == "Open") s.Native.Version = new(0, 11, 12); };
        var result = restore ? await s.Service.TryRestoreAsync(snapshot, snapshot.Version) : await s.Service.TryClearAsync(snapshot.Version);
        result.Status.ShouldBe(ClipboardSnapshotStatus.Changed);
        s.Native.Count("Empty").ShouldBe(0);
        s.Native.Count("Set").ShouldBe(0);
        s.Native.Memory.Values.Where(b => !b.Borrowed).ShouldAllBe(b => b.Freed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Restore_CancellationAroundEmpty_HonorsCommitPoint(bool afterEmpty) => Run(async s =>
    {
        s.Native.Add(13, [1]);
        s.Native.Add(1, [2]);
        var snapshot = await s.Capture();
        using var cts = new CancellationTokenSource();
        if (afterEmpty) s.Native.After = api => { if (api == "Empty") cts.Cancel(); };
        else s.Native.Before = api => { if (api == "Owner") cts.Cancel(); };
        if (afterEmpty)
        {
            var result = await s.Service.TryRestoreAsync(snapshot, snapshot.Version, cts.Token);
            result.Status.ShouldBe(ClipboardSnapshotStatus.Applied);
            s.Native.Count("Set").ShouldBe(2);
        }
        else
        {
            await Should.ThrowAsync<OperationCanceledException>(() => s.Service.TryRestoreAsync(snapshot, snapshot.Version, cts.Token));
            s.Native.Count("Empty").ShouldBe(0);
            s.Native.Count("Set").ShouldBe(0);
        }
    });

    [Theory]
    [InlineData("Alloc")]
    [InlineData("Lock")]
    [InlineData("CopyTo")]
    [InlineData("Unlock")]
    public Task Restore_PreparationFailure_HasNoClipboardWrites(string stage) => Run(async s =>
    {
        s.Native.Add(13, [1]);
        var snapshot = await s.Capture();
        s.Native.Failures[stage] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.ToString().ShouldNotContain("private payload");
        error.InnerException.ShouldBeNull();
        s.Native.Count("Empty").ShouldBe(0);
        s.Native.Count("Set").ShouldBe(0);
        s.Native.Failures.Clear();
        await s.Service.RetryCleanupAsync();
        s.Native.Memory.Values.Where(b => !b.Borrowed).ShouldAllBe(b => b.Freed);
    });

    [Fact]
    public Task Restore_PartialSetFailure_DoesNotFreeTransferredMemory() => Run(async s =>
    {
        s.Native.Add(13, [1]); s.Native.Add(1, [2]); s.Native.Add(7, [3]);
        var snapshot = await s.Capture();
        s.Native.After = api => { if (api == "Set") s.Native.Failures["Set"] = -1; };
        await Should.ThrowAsync<IrisException>(() => s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        s.Native.Memory.Values.Count(b => b.Transferred).ShouldBe(1);
        s.Native.Memory.Values.Count(b => b.Freed).ShouldBe(2);
        s.Native.OwnershipViolations.ShouldBe(0);
    });

    [Fact]
    public Task Cleanup_CloseFreeDestroyFailures_RetainSameThreadResponsibility() => Run(async s =>
    {
        s.Native.Add(13, [1]); s.Native.Add(1, [2]);
        var snapshot = await s.Capture();
        s.Native.Failures["Set"] = -1;
        s.Native.Failures["Close"] = -1;
        s.Native.Failures["Free"] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        error.Message.ShouldContain("FREE"); error.Message.ShouldContain("CLOSE");
        s.Native.Count("Free").ShouldBe(2);
        await Should.ThrowAsync<IrisException>(() => s.Service.ReadVersionAsync());
        s.Native.Failures.Clear();
        s.Native.Failures["Destroy"] = -1;
        await Should.ThrowAsync<IrisException>(() => s.Service.StopAsync());
        s.Native.IsOpen.ShouldBeFalse();
        s.Native.Memory.Values.Where(b => !b.Borrowed).ShouldAllBe(b => b.Freed);
        s.Native.WindowAlive.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(s.Service.Dispose);
        s.Native.Failures.Clear();
        await s.Service.RetryCleanupAsync();
        await s.Service.StopAsync();
        s.Native.WindowAlive.ShouldBeFalse();
        s.Native.Count("Free").ShouldBe(4);
        s.Native.Calls.Select(c => c.Thread).Distinct().Count().ShouldBe(1);
    });

    [Fact]
    public Task Capture_UnlockFailure_HoldsBorrowedHandleAndFrameUntilRetry() => Run(async s =>
    {
        s.Native.Add(13, [1]);
        s.Native.Failures["Unlock"] = -1;
        await Should.ThrowAsync<IrisException>(() => s.Service.CaptureSnapshotAsync());
        s.Native.IsOpen.ShouldBeTrue();
        s.Native.Count("Close").ShouldBe(0);
        s.Native.Count("Free").ShouldBe(0);
        s.Native.Failures.Clear();
        await s.Service.RetryCleanupAsync();
        s.Native.IsOpen.ShouldBeFalse();
        await s.Capture();
    });

    [Theory]
    [InlineData("Create")]
    [InlineData("Pump")]
    [InlineData("Wait")]
    public Task Worker_InitializationOrMessageFailure_CanStopAndRetry(string stage) => Run(async s =>
    {
        s.Native.Failures[stage] = -1;
        if (stage == "Create") await Should.ThrowAsync<IrisException>(() => s.Service.ReadVersionAsync());
        else
        {
            await s.Service.ReadVersionAsync();
            await Until(() => s.Native.FailedCalls.Contains(stage));
            await Should.ThrowAsync<IrisException>(() => s.Service.ReadVersionAsync());
        }
        s.Native.Failures.Clear();
        await s.Service.RetryCleanupAsync();
        await s.Service.ReadVersionAsync();
        s.Native.Count("Create").ShouldBe(stage == "Create" ? 2 : 1);
    });

    [Fact]
    public Task OpenBusy_UsesTenAttempts_AndCancellationBetweenAttempts() => Run(async s =>
    {
        s.Native.Failures["Open"] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => s.Service.ReadVersionAsync());
        error.Code.ShouldBe(ErrorCode.ClipboardBusy);
        s.Native.Count("Open").ShouldBe(10);
        using var cts = new CancellationTokenSource();
        s.Native.Before = api => { if (api == "Open") cts.Cancel(); };
        await Should.ThrowAsync<OperationCanceledException>(() => s.Service.ReadVersionAsync(cts.Token));
        s.Native.Count("Open").ShouldBe(11);
        s.Native.Count("Close").ShouldBe(0);
    });

    [Fact]
    public Task QueuedRestore_BorrowsBeforeCallerDisposes_AndRejectsForeignSnapshot() => Run(async s =>
    {
        s.Native.Add(13, [7, 8]);
        var snapshot = await s.Capture();
        var block = s.Block("Sequence");
        var first = s.Track(s.Service.ReadVersionAsync());
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var restore = s.Track(s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        snapshot.Dispose();
        block.Release.Set();
        await first;
        (await restore).Status.ShouldBe(ClipboardSnapshotStatus.Applied);
        s.Native.Memory[s.Native.Formats[0].Handle].Bytes.ShouldBe(new byte[] { 7, 8 });
        await Should.ThrowAsync<IrisException>(() => s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        using var foreignService = new ClipboardSnapshotService(new FakeClipboardSnapshotNative());
        var live = await s.Capture();
        await Should.ThrowAsync<IrisException>(() => foreignService.TryRestoreAsync(live, live.Version));
    });

    [Fact]
    public Task QueuedCancellation_IsPromptAndQueueIsBounded() => Run(async s =>
    {
        var block = s.Block("Open");
        var first = s.Track(s.Service.ReadVersionAsync());
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        var canceled = s.Track(s.Service.ReadVersionAsync(cts.Token), typeof(OperationCanceledException));
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(10)));
        var queued = new List<Task<ClipboardVersion>>();
        for (int i = 0; i < ClipboardSnapshotService.QueueCapacity; i++) queued.Add(s.Track(s.Service.ReadVersionAsync()));
        var busy = await Should.ThrowAsync<IrisException>(() => s.Service.ReadVersionAsync());
        busy.Code.ShouldBe(ErrorCode.ClipboardBusy);
        block.Release.Set();
        await first;
        await Task.WhenAll(queued);
    });

    [Theory]
    [InlineData("Create")]
    [InlineData("Get")]
    public Task Stop_DuringEnteredNativeCall_WaitsForRealReturn(string stage) => Run(async s =>
        await BlockedLifecycle(s, stage, null));

    private static async Task BlockedLifecycle(Scope s, string stage, Exception? injected)
    {
        s.Native.Add(13, [1]);
        var block = s.Block(stage);
        var read = s.Track(s.Service.CaptureSnapshotAsync(), typeof(OperationCanceledException));
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (injected != null) ExceptionDispatchInfo.Capture(injected).Throw();
        var stop = s.Track(s.Service.StopAsync());
        s.Service.StopAsync().ShouldBeSameAs(stop);
        stop.IsCompleted.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(s.Service.Dispose);
        block.Release.Set();
        await stop;
        read.IsCompleted.ShouldBeTrue();
        await Should.ThrowAsync<OperationCanceledException>(() => read);
        s.Native.WindowAlive.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Get")]
    public async Task TestFailure_AfterNativeEntered_ReleasesGateAndJoinsWorker(string stage)
    {
        var injected = new InvalidOperationException("test body injection");
        var scope = new Scope();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Run(s => BlockedLifecycle(s, stage, injected), scope));
        error.ShouldBeSameAs(injected);
        scope.Joined.ShouldBeTrue();
        scope.Native.WindowAlive.ShouldBeFalse();
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }

    [Theory]
    [InlineData("Get")]
    [InlineData("Size")]
    [InlineData("Lock")]
    [InlineData("CopyFrom")]
    [InlineData("Enum")]
    [InlineData("OwnerPid")]
    public Task Capture_NativeFailure_IsSafeAndNeverFreesBorrowedHandles(string stage) => Run(async s =>
    {
        s.Native.Add(13, [1, 2]);
        s.Native.Version = new(0, 10, 11);
        s.Native.Failures[stage] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => s.Service.CaptureSnapshotAsync());
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.ToString().ShouldNotContain("private");
        error.InnerException.ShouldBeNull();
        s.Native.Count("Free").ShouldBe(0);
        s.Native.Count("Empty").ShouldBe(0);
    });

    [Fact]
    public Task Capture_ZeroSizedHandle_IsNotAnEmptySnapshot() => Run(async s =>
    {
        s.Native.Add(13, [], 0);
        await Should.ThrowAsync<IrisException>(() => s.Service.CaptureSnapshotAsync());
        s.Native.Count("CopyFrom").ShouldBe(0);
    });

    [Fact]
    public Task NativeException_WithSensitiveMessageAndInnerChain_IsSanitized() => Run(async s =>
    {
        s.Native.Before = api =>
        {
            if (api == "Sequence") throw new IrisException(ErrorCode.InvalidArgument, "private clipboard data", new IOException("private path"));
        };
        var error = await Should.ThrowAsync<IrisException>(() => s.Service.ReadVersionAsync());
        error.Code.ShouldBe(ErrorCode.CaptureFailed);
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain("private");
    });

    [Fact]
    public Task StopRetryDispose_DuringDestroy_ShareActualInFlightAttempt() => Run(async s =>
    {
        await s.Service.ReadVersionAsync();
        var gate = s.Block("Destroy");
        var stop = s.Track(s.Service.StopAsync());
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retry = s.Track(s.Service.RetryCleanupAsync());
        var dispose = s.Track(s.Service.DisposeAsync().AsTask());
        s.Service.StopAsync().ShouldBeSameAs(stop);
        retry.IsCompleted.ShouldBeFalse();
        dispose.IsCompleted.ShouldBeFalse();
        s.Native.Count("Destroy").ShouldBe(1);
        gate.Release.Set();
        await Task.WhenAll(stop, retry, dispose);
        s.Native.Count("Destroy").ShouldBe(1);
        s.Native.WindowAlive.ShouldBeFalse();
    });

    [Fact]
    public Task Restore_StopDuringCommit_FinishesAllFormatsBeforeJoining() => Run(async s =>
    {
        s.Native.Add(13, [1]); s.Native.Add(1, [2]);
        var snapshot = await s.Capture();
        var gate = s.Block("Set");
        var restore = s.Track(s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = s.Track(s.Service.StopAsync());
        stop.IsCompleted.ShouldBeFalse();
        gate.Release.Set();
        await stop;
        restore.IsCompleted.ShouldBeTrue();
        (await restore).Status.ShouldBe(ClipboardSnapshotStatus.Applied);
        s.Native.Count("Set").ShouldBe(2);
    });

    [Fact]
    public Task Restore_UnlockAndFreeFailures_KeepAllUnfinishedStages() => Run(async s =>
    {
        s.Native.Add(13, [1]); s.Native.Add(1, [2]);
        var snapshot = await s.Capture();
        int unlocks = 0;
        s.Native.Before = api =>
        {
            if (api == "Unlock" && ++unlocks == 2) s.Native.Failures["Unlock"] = -1;
        };
        s.Native.Failures["Free"] = -1;
        var error = await Should.ThrowAsync<IrisException>(() => s.Service.TryRestoreAsync(snapshot, snapshot.Version));
        error.Message.ShouldContain("UNLOCK_PENDING");
        error.Message.ShouldContain("FREE");
        s.Native.Count("Empty").ShouldBe(0);
        s.Native.Before = null; s.Native.Failures.Clear();
        await s.Service.RetryCleanupAsync();
        s.Native.Memory.Values.Where(b => !b.Borrowed).ShouldAllBe(b => b.Freed && b.Locks == 0);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Stop_DuringClose_CancelsReadButDoesNotUndoCommittedWrite(bool write) => Run(async s =>
    {
        s.Native.Add(13, [1]);
        var version = await s.Service.ReadVersionAsync();
        var gate = s.Block("Close");
        var operation = s.Track(write ? s.Service.TryClearAsync(version) : s.Service.CaptureSnapshotAsync(), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = s.Track(s.Service.StopAsync());
        stop.IsCompleted.ShouldBeFalse();
        gate.Release.Set();
        await stop;
        operation.IsCompleted.ShouldBeTrue();
        if (write) (await operation).Status.ShouldBe(ClipboardSnapshotStatus.Applied);
        else await Should.ThrowAsync<OperationCanceledException>(() => operation);
    });

    [Fact]
    public async Task TestBodyAndCleanupErrors_AreBothRetainedAfterWorkerJoined()
    {
        var scope = new Scope();
        var body = new InvalidOperationException("test body error");
        var cleanup = new IOException("test cleanup error");
        scope.AfterCleanup = () => throw cleanup;
        var error = await Should.ThrowAsync<AggregateException>(() => Run(s => BlockedLifecycle(s, "Get", body), scope));
        error.InnerExceptions.ShouldContain(body);
        error.InnerExceptions.ShouldContain(cleanup);
        scope.Joined.ShouldBeTrue();
        scope.Native.WindowAlive.ShouldBeFalse();
    }

    private static async Task Run(Func<Scope, Task> body, Scope? existing = null)
    {
        var scope = existing ?? new Scope();
        Exception? primary = null;
        var errors = new List<Exception>();
        try { await body(scope); }
        catch (Exception ex) { primary = ex; }
        finally
        {
            scope.Native.Before = null; scope.Native.After = null; scope.Native.Failures.Clear();
            foreach (var gate in scope.Gates) gate.Release.Set();
            try { await scope.Service.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var (task, expected) in scope.Tasks)
                try
                {
                    await task;
                    if (task is Task<ClipboardSnapshotResult> capture) (await capture).Snapshot?.Dispose();
                }
                catch (Exception ex) { if (expected == null || !expected.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await scope.Service.RetryCleanupAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await scope.Service.DisposeAsync(); scope.Joined = true; } catch (Exception ex) { errors.Add(ex); }
            foreach (var snapshot in scope.Snapshots) snapshot.Dispose();
            foreach (var gate in scope.Gates) gate.Release.Dispose();
            try
            {
                scope.Native.OwnershipViolations.ShouldBe(0);
                scope.Native.IsOpen.ShouldBeFalse();
                scope.Native.Memory.Values.ShouldAllBe(b => b.Locks == 0 && (b.Borrowed || b.Freed || b.Transferred));
            }
            catch (Exception ex) { errors.Add(ex); }
            try { scope.AfterCleanup?.Invoke(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    private sealed class Scope
    {
        public FakeClipboardSnapshotNative Native { get; } = new();
        public ClipboardSnapshotService Service { get; }
        public List<ClipboardSnapshot> Snapshots { get; } = [];
        public List<(Task Task, Type? Expected)> Tasks { get; } = [];
        public List<NativeGate> Gates { get; } = [];
        public bool Joined { get; set; }
        public Action? AfterCleanup { get; set; }
        public Scope() => Service = new(Native);
        public T Track<T>(T task, Type? expected = null) where T : Task { Tasks.Add((task, expected)); return task; }
        public async Task<ClipboardSnapshot> Capture()
        {
            var result = await Service.CaptureSnapshotAsync();
            if (result.Snapshot != null) Snapshots.Add(result.Snapshot);
            result.Status.ShouldBe(ClipboardSnapshotStatus.Success);
            return result.Snapshot.ShouldNotBeNull();
        }
        public NativeGate Block(string stage)
        {
            var gate = new NativeGate();
            Gates.Add(gate);
            int entered = 0;
            Native.Before = api =>
            {
                if (api == stage && Interlocked.Exchange(ref entered, 1) == 0)
                {
                    gate.Entered.TrySetResult();
                    gate.Release.Wait();
                }
            };
            return gate;
        }
    }
    private sealed class NativeGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
    }
}
