using Iris.Core.Errors;
using Iris.Platform.Windows.Selection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class KeyboardPreparationTests
{
    [Fact]
    public Task Preparation_RunsOnceOnlyAfterUserReleasesKeys() => SelectionTestScope.Run(async s =>
    {
        s.Keys.KeyStates[0xA2] = unchecked((short)0x8000);
        int prepared = 0;
        var copy = s.Track(s.Keyboard.CopyWithPreparationAsync(new(0x1234, 5000), _ => { prepared++; return Task.FromResult(false); }, () => true));
        await s.Time.WaitForDelayAsync(10); prepared.ShouldBe(0);
        s.Keys.KeyStates.Clear(); s.Time.Advance(TimeSpan.FromMilliseconds(10));
        (await copy).Outcome.ShouldBe(KeyboardCopyOutcome.NotReady);
        prepared.ShouldBe(1); s.Keys.RecordedInputs.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task GuardBeforeCDown_RejectsOrThrows_AndCompensatesOnlyCtrl(bool throws) => SelectionTestScope.Run(async s =>
    {
        int calls = 0;
        bool Guard()
        {
            if (++calls != 3) return true;
            if (throws) throw new IOException("private focus detail");
            return false;
        }
        var result = await s.Keyboard.CopyWithPreparationAsync(new(0x1234, 5000), _ => Task.FromResult(true), Guard);
        result.Outcome.ShouldBe(throws ? KeyboardCopyOutcome.Failed : KeyboardCopyOutcome.TargetChanged);
        result.MayHaveCopied.ShouldBeFalse(); s.Copies.ShouldBe(0);
        s.Keys.RecordedInputs.Count.ShouldBe(2);
        s.Keys.RecordedInputs.ShouldAllBe(i => (int)i.Input.Anonymous.ki.wVk == 0xA2);
        s.Keyboard.HasPendingRelease.ShouldBeFalse(); result.ToString().ShouldNotContain("private focus detail");
    });

    [Fact]
    public Task PreparationException_IsSafeAndDoesNotSend() => SelectionTestScope.Run(async s =>
    {
        var error = await Should.ThrowAsync<IrisException>(() => s.Keyboard.CopyWithPreparationAsync(new(0x1234, 5000),
            _ => throw new IOException("private preparation"), () => true));
        error.InnerException.ShouldBeNull(); error.ToString().ShouldNotContain("private preparation"); s.Copies.ShouldBe(0);
    });

    [Fact]
    public Task Stop_WaitsForActualPreparation_AndRetainsCancellationCallbackError() => SelectionTestScope.Run(async s =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        s.Release.Add(() => release.TrySetResult(true));
        var copy = s.Track(s.Keyboard.CopyWithPreparationAsync(new(0x1234, 5000), token =>
        {
            registration = token.Register(() => { canceled.TrySetResult(); throw new IOException("private cancel error"); });
            entered.TrySetResult(); return release.Task;
        }, () => true), typeof(OperationCanceledException));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = s.Track(s.Keyboard.StopAsync(), typeof(IrisException));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.IsCompleted.ShouldBeFalse(); copy.IsCompleted.ShouldBeFalse();
        release.TrySetResult(true);
        var error = await Should.ThrowAsync<IrisException>(() => stop);
        error.ToString().ShouldNotContain("private cancel error");
        await Should.ThrowAsync<OperationCanceledException>(() => copy);
        registration.Dispose(); s.Copies.ShouldBe(0);
    });
}
