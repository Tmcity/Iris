using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Platform.Windows.SystemIntegration;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void GetCurrentUserMutexName_StartsWithPrefix_AndEndsWithSid()
    {
        var name = SingleInstanceGuard.GetCurrentUserMutexName();
        var sid = WindowsIdentity.GetCurrent().User?.Value;

        sid.ShouldNotBeNull();
        name.ShouldStartWith(IpcConstants.MutexNamePrefix);
        name.ShouldEndWith(sid!);
    }

    [Fact]
    public void RepeatedDispose_DoesNotThrow()
    {
        var testMutexName = "Local\\IrisTestMutex_" + Guid.NewGuid().ToString("N");
        var guard = SingleInstanceGuard.TryAcquire(testMutexName);
        guard.ShouldNotBeNull();

        // Repeated dispose
        Should.NotThrow(() =>
        {
            guard.Dispose();
            guard.Dispose();
            guard.Dispose();
        });
    }

    [Fact]
    public async Task TwoThreads_ThreadAHolds_ThreadBReturnsNull_AfterAReleased_ThreadBReturnsGuard()
    {
        var testMutexName = "Local\\IrisTestMutex_" + Guid.NewGuid().ToString("N");

        var aAcquiredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aReleaseTrigger = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aDisposedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ct = TestContext.Current.CancellationToken;

        // Thread A
        var threadA = Task.Run(() =>
        {
            using var guardA = SingleInstanceGuard.TryAcquire(testMutexName);
            guardA.ShouldNotBeNull();
            aAcquiredTcs.SetResult(true);

            // Wait until signaled to release
            aReleaseTrigger.Task.Wait(TimeSpan.FromSeconds(5));
            guardA.Dispose();
            aDisposedTcs.SetResult(true);
        }, ct);

        await aAcquiredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Thread B tries while Thread A holds it
        SingleInstanceGuard? guardBWhileAHolds = null;
        await Task.Run(() =>
        {
            guardBWhileAHolds = SingleInstanceGuard.TryAcquire(testMutexName);
        }, ct);
        guardBWhileAHolds.ShouldBeNull();

        // Signal Thread A to release and wait for it
        aReleaseTrigger.SetResult(true);
        await aDisposedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await threadA;

        // Thread B tries after Thread A released
        SingleInstanceGuard? guardBAfterRelease = null;
        await Task.Run(() =>
        {
            guardBAfterRelease = SingleInstanceGuard.TryAcquire(testMutexName);
        }, ct);

        try
        {
            guardBAfterRelease.ShouldNotBeNull();
        }
        finally
        {
            guardBAfterRelease?.Dispose();
        }
    }

    [Fact]
    public async Task ThreadA_TerminatesWithoutRelease_ThreadBAcquiresViaAbandonedMutex()
    {
        var testMutexName = "Local\\IrisTestMutex_" + Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        var threadStarted = new ManualResetEventSlim(false);

        // Dedicated foreground/background Thread that does NOT dispose the guard and terminates
        var threadA = new Thread(() =>
        {
            var guard = SingleInstanceGuard.TryAcquire(testMutexName);
            guard.ShouldNotBeNull();
            threadStarted.Set();
            // Terminate thread without releasing or disposing
        })
        {
            IsBackground = true,
        };

        threadA.Start();
        threadStarted.Wait(TimeSpan.FromSeconds(5), ct);
        threadA.Join(TimeSpan.FromSeconds(5));

        // Thread B on thread pool tries to acquire - should succeed via AbandonedMutexException
        SingleInstanceGuard? guardB = null;
        await Task.Run(() =>
        {
            guardB = SingleInstanceGuard.TryAcquire(testMutexName);
        }, ct);

        try
        {
            guardB.ShouldNotBeNull();
        }
        finally
        {
            guardB?.Dispose();
        }
    }
}
