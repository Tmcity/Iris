using Iris.Core.Platform;
using Iris.Platform.Windows.Hooks;
using Shouldly;
using Windows.Win32.Foundation;

namespace Iris.Platform.Windows.Tests.Hooks;

public sealed class MousePipelineTests
{
    [Fact]
    public Task Capacity_Exactly256PendingSamplesAreAccepted() => MouseTestRig.Use(async rig =>
    {
        var gesture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Monitor.SelectionGestureDetected += (_, _) => gesture.TrySetResult();
        await rig.Start();
        rig.Desktop.SourceGate = rig.Gate();
        await rig.Send(MouseMessage.LeftDown);
        await rig.Desktop.SourceGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 255; i++) await rig.Send(MouseMessage.Move, 20);
        await rig.Send(MouseMessage.LeftUp, 20);
        rig.Desktop.SourceGate.Release();
        await gesture.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Invalidations.ShouldBeEmpty(); rig.Gestures.Single().Gesture.ShouldBe(SelectionGesture.Drag);
    });

    [Fact]
    public Task ClickPipeline_PublishesDoubleThenTripleButNotFourth() => MouseTestRig.Use(async rig =>
    {
        await rig.Start();
        for (int i = 0; i < 4; i++) { await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp); }
        await rig.DrainAsync();
        rig.Gestures.Select(e => e.Gesture).ToArray().ShouldBe(new[] { SelectionGesture.DoubleClick, SelectionGesture.TripleClick });
    });

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    public Task CursorAtDown_IsCapturedAndRequirementCanBeDisabled(bool required, bool available, int gestures) => MouseTestRig.Use(async rig =>
    {
        rig.Api.CursorSuccess = available;
        await rig.Start(MouseTestRig.Options(required));
        await rig.Send(MouseMessage.LeftDown);
        rig.Api.CursorSuccess = !available;
        await rig.Send(MouseMessage.LeftUp, 4); await rig.DrainAsync();
        rig.Gestures.Count.ShouldBe(gestures);
    });

    [Fact]
    public Task ShiftAtDown_IsRetainedAfterKeyRelease() => MouseTestRig.Use(async rig =>
    {
        rig.Api.Shift = short.MinValue;
        await rig.Start(); await rig.Send(MouseMessage.LeftDown);
        rig.Api.Shift = 0;
        await rig.Send(MouseMessage.LeftUp); await rig.DrainAsync();
        rig.Gestures.Single().Gesture.ShouldBe(SelectionGesture.ShiftClick);
    });

    [Theory]
    [InlineData(250, 1)]
    [InlineData(251, 0)]
    public Task NativeAge_Includes250AndRejects251(uint age, int gestures) => MouseTestRig.Use(async rig =>
    {
        rig.Api.Now = 100 + age;
        await rig.Start();
        await rig.Send(MouseMessage.LeftDown, time: 100); await rig.Send(MouseMessage.LeftUp, 4, time: 100); await rig.DrainAsync();
        rig.Gestures.Count.ShouldBe(gestures);
    });

    [Fact]
    public Task UnknownMetrics_DoNotGuessDoubleClickButStillAllowDrag() => MouseTestRig.Use(async rig =>
    {
        rig.Desktop.Dpi = double.NaN;
        await rig.Start();
        for (int i = 0; i < 3; i++) { await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp); }
        await rig.DrainAsync(); rig.Gestures.ShouldBeEmpty();
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 4); await rig.DrainAsync();
        rig.Gestures.Single().Gesture.ShouldBe(SelectionGesture.Drag);
        rig.Api.MetricCalls.ShouldBeEmpty();
    });

    [Fact]
    public Task SourceChangesBetweenDownAndUp_CannotCompleteDrag() => MouseTestRig.Use(async rig =>
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Desktop.AfterSource = () => observed.TrySetResult();
        await rig.Start(); await rig.Send(MouseMessage.LeftDown);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Api.Window = 2000;
        // Use native up identity mismatch; worker may see either snapshot, neither can authorize the pair.
        await rig.Send(MouseMessage.LeftUp, 20); await rig.DrainAsync();
        rig.Gestures.ShouldBeEmpty();
    });

    [Fact]
    public Task CallNextFailure_IsContainedAndAttemptedExactlyOnce() => MouseTestRig.Use(async rig =>
    {
        await rig.Start(); rig.Api.ThrowAt = nameof(rig.Api.CallNext);
        rig.Api.LastCallback!(-1, new WPARAM(0x201), new LPARAM(0)).Value.ShouldBe((nint)0);
        rig.Api.Calls.Count(c => c.Name == nameof(rig.Api.CallNext)).ShouldBe(1);
        rig.Api.ThrowAt = null;
        await rig.DrainAsync(); rig.Gestures.ShouldBeEmpty();
    });

    [Fact]
    public Task CancellationAfterSuccessfulStart_DoesNotRevokeRun() => MouseTestRig.Use(async rig =>
    {
        using var cts = new CancellationTokenSource();
        await rig.Start(token: cts.Token);
        cts.Cancel();
        rig.Monitor.IsRunning.ShouldBeTrue(); rig.Monitor.Generation.ShouldBe(1);
        await rig.Send(MouseMessage.LeftDown); await rig.Send(MouseMessage.LeftUp, 4); await rig.DrainAsync();
        rig.Gestures.Count.ShouldBe(1);
    });
}
