using Iris.App.Updates;
using Iris.Core.Errors;
using Shouldly;

namespace Iris.App.Tests.Updates;

public sealed class UpdateHandoffTests
{
    [Fact]
    public async Task RevokeCancelsHeldPreparationBeforeWaitingForSemaphoreAndJoinsIt()
    {
        var environment = new EnvironmentBoundary();
        await using var handoff = new UpdateHandoff(environment);
        var preparing = handoff.PrepareAsync(new(Guid.NewGuid(), "unused", 1, new string('A', 64), "1.0.0"), TestContext.Current.CancellationToken);
        Task? revoke = null;
        try
        {
            await environment.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            revoke = handoff.RevokeAsync();
            environment.Token.IsCancellationRequested.ShouldBeTrue(); revoke.IsCompleted.ShouldBeFalse(); preparing.IsCompleted.ShouldBeFalse();
            environment.Release.TrySetResult();
            var ex = await Should.ThrowAsync<IrisException>(() => preparing);
            ex.Code.ShouldBe(ErrorCode.Unknown);
            await revoke;
            handoff.IsPrepared.ShouldBeFalse(); environment.HelperReads.ShouldBe(0);
        }
        finally { environment.Release.TrySetResult(); try { await preparing; } catch (Exception) { } if (revoke != null) await revoke; }
    }

    [Fact]
    public async Task CallerCancellationThrowsOperationCanceledException()
    {
        var environment = new EnvironmentBoundary();
        await using var handoff = new UpdateHandoff(environment);
        using var callerCts = new CancellationTokenSource();
        var preparing = handoff.PrepareAsync(new(Guid.NewGuid(), "unused", 1, new string('A', 64), "1.0.0"), callerCts.Token);
        try
        {
            await environment.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            callerCts.Cancel();
            environment.Release.TrySetResult();
            await Should.ThrowAsync<OperationCanceledException>(() => preparing);
            handoff.IsPrepared.ShouldBeFalse();
        }
        finally { environment.Release.TrySetResult(); try { await preparing; } catch (Exception) { } }
    }

    private sealed class EnvironmentBoundary : UpdateHandoffEnvironment
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token; internal int HelperReads;
        internal override void ValidateInstallation() { }
        internal override async Task ValidateCandidateAsync(UpdateCandidate candidate, CancellationToken token)
        { Token = token; Entered.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested(); }
        internal override string HelperPath { get { HelperReads++; throw new InvalidOperationException("No work may start after cancellation."); } }
    }
}
