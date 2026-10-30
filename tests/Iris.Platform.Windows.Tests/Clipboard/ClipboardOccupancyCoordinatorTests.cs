using System.Collections.Concurrent;
using Iris.Clipboard.IsolatedHelper;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class ClipboardOccupancyCoordinatorTests
{
    [Fact]
    public void WorkAndCleanupEndBeforeOwnerRelease_AndAllOwnerOperationsUseOneSeparateThread()
    {
        var native = new OccupancyNative();
        int coordinator = Environment.CurrentManagedThreadId;
        ClipboardOccupancyCoordinator.Run(native, () => native.Calls.Enqueue(("actual-work-completed", coordinator)), TimeSpan.FromSeconds(5));
        var calls = native.Calls.ToArray();
        Array.FindIndex(calls, c => c.Name == "actual-work-completed").ShouldBeLessThan(Array.FindIndex(calls, c => c.Name == "Close"));
        int owner = calls.Single(c => c.Name == "Bind").Thread;
        owner.ShouldNotBe(coordinator);
        calls.Where(c => c.Name is "Bind" or "Create" or "Open" or "Close" or "Destroy" or "Restore").Select(c => c.Thread).Distinct().ShouldBe([owner]);
        calls.Last().Name.ShouldBe("owner-joined");
    }

    [Fact]
    public void RejectedOpenDoesNotRunWorkOrClose_ButDestroysAndRestoresThenJoins()
    {
        var native = new OccupancyNative { OpenResult = false };
        bool work = false;
        Should.Throw<AggregateException>(() => ClipboardOccupancyCoordinator.Run(native, () => work = true, TimeSpan.FromSeconds(5)));
        work.ShouldBeFalse();
        native.Calls.Select(c => c.Name).ShouldNotContain("Close");
        native.Calls.Select(c => c.Name).ShouldContain("Destroy");
        native.Calls.Select(c => c.Name).ShouldContain("Restore");
        native.Calls.Last().Name.ShouldBe("owner-joined");
    }

    [Fact]
    public void PrimaryWorkErrorAndBothCleanupFailuresAreRetainedAfterJoin()
    {
        var native = new OccupancyNative { CloseResult = false, DestroyResult = false };
        var primary = new IOException("owned work failure");
        var error = Should.Throw<AggregateException>(() => ClipboardOccupancyCoordinator.Run(native, () => throw primary, TimeSpan.FromSeconds(5)));
        error.InnerExceptions.ShouldContain(primary);
        error.InnerExceptions.Count.ShouldBe(3);
        native.Calls.Last().Name.ShouldBe("owner-joined");
    }

    [Fact]
    public void ReadyTimeoutStillReleasesAndJoinsTheLateOwner()
    {
        using var releaseBind = new ManualResetEventSlim();
        using var bindEntered = new ManualResetEventSlim();
        var native = new OccupancyNative { BindGate = releaseBind, BindEntered = bindEntered };
        try
        {
            var error = Should.Throw<AggregateException>(() => ClipboardOccupancyCoordinator.Run(native, () => throw new InvalidOperationException("Work must not run"), TimeSpan.FromSeconds(5), (_, _) =>
            {
                bindEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                releaseBind.Set();
                return false; // Deterministic outer readiness timeout while the owner still must clean up.
            }));
            error.InnerExceptions.Any(e => e is TimeoutException).ShouldBeTrue();
            native.Calls.Last().Name.ShouldBe("owner-joined");
        }
        finally { releaseBind.Set(); }
    }

    private sealed class OccupancyNative : IClipboardOccupancyNative
    {
        internal readonly ConcurrentQueue<(string Name, int Thread)> Calls = new();
        internal bool OpenResult = true, CloseResult = true, DestroyResult = true;
        internal ManualResetEventSlim? BindGate;
        internal ManualResetEventSlim? BindEntered;
        private void Add(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId));
        public void Bind() { Add("Bind"); BindEntered?.Set(); BindGate?.Wait(); }
        public nint CreateWindow() { Add("Create"); return 1; }
        public bool Open(nint window) { Add("Open"); return OpenResult; }
        public bool Close() { Add("Close"); return CloseResult; }
        public bool Destroy(nint window) { Add("Destroy"); return DestroyResult; }
        public void Restore() => Add("Restore");
        public void Record(string action) => Add(action);
    }
}
