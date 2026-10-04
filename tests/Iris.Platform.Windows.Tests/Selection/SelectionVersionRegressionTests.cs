using System.Reflection;
using Iris.Core.Errors;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Selection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class SelectionVersionRegressionTests
{
    private static readonly uint[] Formats = [13, 0xC001, 0xC002];
    private static void SeedFormats(SelectionTestScope s)
    {
        s.Seed();
        s.Native.Names[0xC001] = "Rich Text Format";
        s.Native.Names[0xC002] = "PNG";
        s.Native.Add(0xC001, [0, 17, 255, 128]);
        s.Native.Add(0xC002, [23, 0, 11, 255]);
    }
    private static void AssertFormats(SelectionTestScope s)
    {
        s.Native.Formats.Select(f => f.Format).ShouldBe(Formats);
        s.ClipboardText().ShouldBe("old text");
        s.Native.Memory[s.Native.Formats[1].Handle].Bytes.ShouldBe(new byte[] { 0, 17, 255, 128 });
        s.Native.Memory[s.Native.Formats[2].Handle].Bytes.ShouldBe(new byte[] { 23, 0, 11, 255 });
    }
    private static object? Pending(SelectionTestScope s) => typeof(CompositeSelectionReader)
        .GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s.Reader);
    private static ClipboardSnapshot PendingSnapshot(object pending) => (ClipboardSnapshot)pending.GetType().GetProperty("Snapshot")!.GetValue(pending)!;
    private static ClipboardWriteReceipt PendingReceipt(object pending) => (ClipboardWriteReceipt)pending.GetType().GetProperty("Restore")!.GetValue(pending)!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task Restore_OwnerPidFaultAfterEmptyOrSet_DoesNotInterruptPublication(int afterSet) => SelectionTestScope.Run(async s =>
    {
        SeedFormats(s); s.CopyOnSend("candidate");
        int empty = 0, sets = 0, pidCallsAtFault = -1;
        s.Native.After = api =>
        {
            if (api == "Empty") empty++;
            if (api == "Set") sets++;
            if ((api == "Empty" && empty == 2 && afterSet == 0) || (api == "Set" && sets == afterSet))
            {
                pidCallsAtFault = s.Native.Count("OwnerPid");
                s.Native.Failures["OwnerPid"] = -1;
            }
        };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Tick(20);
        (await read)!.Text.ShouldBe("candidate");
        pidCallsAtFault.ShouldBeGreaterThan(0);
        s.Native.Count("OwnerPid").ShouldBe(pidCallsAtFault);
        AssertFormats(s); Pending(s).ShouldBeNull(); s.Copies.ShouldBe(1);
        s.Native.Failures.Clear(); s.Native.After = null;
        await s.Reader.RetryCleanupAsync();
        s.Native.Count("Set").ShouldBe(3); AssertFormats(s);
    });

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public Task Receipt_SequenceObservationFailure_InvalidatesPreviousStamp(bool restore, int afterSet) => SelectionTestScope.Run(async s =>
    {
        SeedFormats(s);
        var captured = await s.Clipboard.CaptureSnapshotAsync();
        var snapshot = captured.Snapshot!; s.Snapshots.Add(snapshot);
        var receipt = new ClipboardWriteReceipt();
        bool fail = false; int sets = 0;
        s.Native.After = api =>
        {
            if (api == "Set") sets++;
            if ((api == "Empty" && afterSet == 0) || (api == "Set" && sets == afterSet)) fail = true;
        };
        s.Native.Before = api => { if (api == "Sequence" && fail) throw new IOException("injected version observation"); };
        Task write = restore
            ? s.Clipboard.TryRestoreForSelectionAsync(snapshot, captured.Version, receipt, CancellationToken.None)
            : s.Clipboard.TryClearForSelectionAsync(captured.Version, receipt, CancellationToken.None);
        await Should.ThrowAsync<IrisException>(() => write);
        receipt.Written.ShouldBeTrue(); receipt.Version.ShouldBeNull();
        receipt.AllFormatsPublished.ShouldBe(!restore || afterSet == 3);
        s.Native.IsOpen.ShouldBeFalse();
        using (var lease = snapshot.Borrow(s.Clipboard))
        {
            lease.Count.ShouldBe(3); lease.FormatAt(1).ShouldBe(0xC001u);
            lease.BytesAt(1).ToArray().ShouldBe(new byte[] { 0, 17, 255, 128 });
        }
        s.Native.Before = null; s.Native.After = null;
        await s.Clipboard.RetryCleanupAsync();
        receipt.Version.ShouldBeNull(); // Cleanup cannot invent a missing authorization stamp.
        s.Native.Count("Set").ShouldBe(restore ? afterSet : 0);
        s.Copies.ShouldBe(0);
    });

    [Fact]
    public Task WriteOwnerIdentityFailure_IsBeforeAnyDestructiveWriteAndCanRetry() => SelectionTestScope.Run(async s =>
    {
        SeedFormats(s);
        var captured = await s.Clipboard.CaptureSnapshotAsync(); s.Snapshots.Add(captured.Snapshot!);
        var receipt = new ClipboardWriteReceipt();
        s.Native.Failures["OwnerPid"] = -1;
        await Should.ThrowAsync<IrisException>(() => s.Clipboard.TryRestoreForSelectionAsync(captured.Snapshot!, captured.Version, receipt, CancellationToken.None));
        receipt.Written.ShouldBeFalse(); s.Native.Count("Empty").ShouldBe(0); s.Native.Count("Set").ShouldBe(0);
        AssertFormats(s);
        s.Native.Failures.Clear();
        var result = await s.Clipboard.TryRestoreForSelectionAsync(captured.Snapshot!, captured.Version, receipt, CancellationToken.None);
        result.Status.ShouldBe(ClipboardSnapshotStatus.Applied);
        receipt.Version!.Value.Owner.ShouldBe(900); receipt.Version.Value.OwnerProcessId.ShouldBe(42u);
        AssertFormats(s);
    });

    [Theory]
    [InlineData("Sequence")]
    [InlineData("Close")]
    public Task FullyPublishedRestore_ObservationOrCloseFailure_RetryOnlyFinishesCleanup(string failure) => SelectionTestScope.Run(async s =>
    {
        SeedFormats(s); s.CopyOnSend("candidate");
        int sets = 0; bool armed = false;
        s.Native.After = api => { if (api == "Set" && ++sets == 3) armed = true; };
        s.Native.Before = api =>
        {
            if (!armed || api != failure) return;
            if (failure == "Sequence") throw new IOException("injected final observation");
            s.Native.Failures["Close"] = -1;
        };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Tick(20); await Should.ThrowAsync<IrisException>(() => read);
        var pending = Pending(s).ShouldNotBeNull();
        var snapshot = PendingSnapshot(pending);
        PendingReceipt(pending).AllFormatsPublished.ShouldBeTrue();
        if (failure == "Sequence") PendingReceipt(pending).Version.ShouldBeNull();
        using (var lease = snapshot.Borrow(s.Clipboard)) lease.Count.ShouldBe(3);
        AssertFormats(s);
        s.Native.Before = null; s.Native.After = null; s.Native.Failures.Clear();
        await s.Reader.RetryCleanupAsync();
        Pending(s).ShouldBeNull(); s.Native.Count("Set").ShouldBe(3); s.Copies.ShouldBe(1); AssertFormats(s);
        Should.Throw<IrisException>(() => snapshot.Borrow(s.Clipboard));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PartialRestore_WithTrustedStamp_RetriesOrPreservesActualExternalUpdate(bool external) => SelectionTestScope.Run(async s =>
    {
        SeedFormats(s); s.CopyOnSend("candidate");
        s.Native.After = api => { if (api == "Set") s.Native.Failures["Set"] = -1; };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Tick(20); await Should.ThrowAsync<IrisException>(() => read);
        var pending = Pending(s).ShouldNotBeNull();
        var receipt = PendingReceipt(pending);
        receipt.AllFormatsPublished.ShouldBeFalse(); receipt.Version.ShouldBe(s.Native.Version);
        using (var lease = PendingSnapshot(pending).Borrow(s.Clipboard)) lease.Count.ShouldBe(3);
        s.Native.After = null; s.Native.Failures.Clear();
        int writes = s.Native.Count("Set"), clears = s.Native.Count("Empty");
        if (external) s.ExternalCopy("external", 999, 77);
        await s.Reader.RetryCleanupAsync(); Pending(s).ShouldBeNull(); s.Copies.ShouldBe(1);
        if (external)
        {
            s.ClipboardText().ShouldBe("external"); s.Native.Count("Set").ShouldBe(writes); s.Native.Count("Empty").ShouldBe(clears);
        }
        else AssertFormats(s);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task SameSourceDelayedRendering_ReobservesWithinOriginalBudgetAndRestores(int changes) => SelectionTestScope.Run(async s =>
    {
        SeedFormats(s); s.CopyOnSend("rendered");
        int remaining = changes;
        s.Native.After = api =>
        {
            if (api == "CopyFrom" && s.Native.Version.OwnerProcessId == 5000 && remaining-- > 0)
                s.Native.Version = s.Native.Version with { Sequence = unchecked(s.Native.Version.Sequence + 1) };
        };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        await s.Time.WaitForDelayAsync(20);
        long start = s.Time.GetTimestamp();
        s.Time.Advance(TimeSpan.FromMilliseconds(20));
        for (int i = 0; i < changes; i++) await s.Tick(20);
        (await read)!.Text.ShouldBe("rendered");
        s.Time.GetElapsedTime(start).TotalMilliseconds.ShouldBe((changes + 1) * 20);
        s.Copies.ShouldBe(1); s.Native.Count("Empty").ShouldBe(2); AssertFormats(s);
    });

    [Theory]
    [InlineData("external")]
    [InlineData("unknown")]
    [InlineData("cancel")]
    [InlineData("focus")]
    [InlineData("timeout")]
    public Task ChangedText_RejectsExternalOrInterruptedReobservation(string change) => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("candidate"); using var cancellation = new CancellationTokenSource();
        bool changed = false;
        s.Native.After = api =>
        {
            if (api != "CopyFrom" || s.Native.Version.OwnerProcessId != 5000 || changed) return;
            changed = true;
            if (change == "external") s.ExternalCopy("external", 999, 77);
            else if (change == "unknown") s.ExternalCopy("unknown", 0, 0);
            else s.Native.Version = s.Native.Version with { Sequence = unchecked(s.Native.Version.Sequence + 1) };
        };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), cancellation.Token), typeof(OperationCanceledException));
        await s.Tick(20);
        if (change is not "external" and not "unknown")
        {
            await s.Time.WaitForDelayAsync(20);
            if (change == "cancel") cancellation.Cancel();
            else if (change == "focus") { s.Focus.Focus++; s.Time.Advance(TimeSpan.FromMilliseconds(20)); }
            else s.Time.Advance(TimeSpan.FromMilliseconds(80));
        }
        if (change == "cancel") await Should.ThrowAsync<OperationCanceledException>(() => read);
        else (await read).ShouldBeNull();
        s.Copies.ShouldBe(1); s.Native.Count("Get").ShouldBe(2); s.Native.Count("Empty").ShouldBe(1);
    });

    [Fact]
    public Task ContinuousDelayedRendering_ExhaustsOneBudgetWithoutRecopy() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend("changing");
        s.Native.After = api =>
        {
            if (api == "CopyFrom" && s.Native.Version.OwnerProcessId == 5000)
                s.Native.Version = s.Native.Version with { Sequence = unchecked(s.Native.Version.Sequence + 1) };
        };
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None));
        for (int i = 0; i < 5; i++) await s.Tick(20);
        (await read).ShouldBeNull(); s.Copies.ShouldBe(1); s.Native.Count("Get").ShouldBe(5); s.Native.Count("Empty").ShouldBe(1);
    });
}
