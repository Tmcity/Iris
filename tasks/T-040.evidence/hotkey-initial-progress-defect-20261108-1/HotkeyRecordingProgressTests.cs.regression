using Iris.App.Hosting;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.App.Tests.Hosting;

public sealed class HotkeyRecordingProgressTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitialCaptureProgress_PublishesInitializedSnapshotWithoutAnotherKey(bool ready)
    {
        var service = new InitializedCaptureService(ready);
        await using var coordinator = new HotkeyRecordingCoordinator(captureService: service);
        var observed = new List<HotkeyCaptureProgress>();
        var recording = coordinator.RecordAsync("initial-progress", 0x1234, observed.Add, TestContext.Current.CancellationToken);
        try
        {
            observed.Count.ShouldBe(1);
            observed[0].IsReady.ShouldBe(ready);
            observed[0].Revision.ShouldBe(1);
        }
        finally
        {
            service.Session.Cancel();
            await recording.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task InitialCaptureProgress_ObserverFailureDoesNotPreventCaptureCleanup()
    {
        var service = new InitializedCaptureService(true);
        await using var coordinator = new HotkeyRecordingCoordinator(captureService: service);
        int attempts = 0;
        var recording = coordinator.RecordAsync("failing-observer", 0x1234, _ =>
        {
            attempts++;
            throw new InvalidOperationException("Synthetic observer failure");
        }, TestContext.Current.CancellationToken);
        try
        {
            attempts.ShouldBe(1);
            recording.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            service.Session.Cancel();
            var result = await recording.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            result.Outcome.ShouldBe(HotkeyCaptureOutcome.Canceled);
            service.Session.Disposed.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task InitialCaptureProgress_CompletedCaptureReleasesItsObserver()
    {
        var service = new InitializedCaptureService(true);
        await using var coordinator = new HotkeyRecordingCoordinator(captureService: service);
        int updates = 0;
        var recording = coordinator.RecordAsync("observer-lifetime", 0x1234, _ => updates++, TestContext.Current.CancellationToken);
        service.Session.SubscriberCount.ShouldBe(1);
        service.Session.Cancel();
        await recording.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        service.Session.SubscriberCount.ShouldBe(0);
        int completedUpdates = updates;
        service.Session.Publish(new(2, true, HotkeyModifiers.Control));
        updates.ShouldBe(completedUpdates);
    }

    private sealed class InitializedCaptureService(bool ready) : IHotkeyCaptureService
    {
        public InitializedCaptureSession Session { get; } = new();
        public Task<IHotkeyCaptureSession> BeginAsync(nint ownerWindowHandle, CancellationToken cancellationToken = default)
        {
            Session.Publish(new(1, ready, HotkeyModifiers.None));
            return Task.FromResult<IHotkeyCaptureSession>(Session);
        }
        public Task StopAsync() { Session.Cancel(); return Task.CompletedTask; }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InitializedCaptureSession : IHotkeyCaptureSession
    {
        private readonly TaskCompletionSource<HotkeyCaptureResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler? _changed;
        public Guid Id { get; } = Guid.NewGuid();
        public HotkeyCaptureProgress Current { get; private set; } = new(0, false, HotkeyModifiers.None);
        public Task<HotkeyCaptureResult> Completion => _completion.Task;
        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
        public bool Disposed { get; private set; }
        public event EventHandler? ProgressChanged { add => _changed += value; remove => _changed -= value; }
        public void Publish(HotkeyCaptureProgress progress) { Current = progress; _changed?.Invoke(this, EventArgs.Empty); }
        public void Cancel() => _completion.TrySetResult(new(HotkeyCaptureOutcome.Canceled));
        public ValueTask DisposeAsync() { Disposed = true; Cancel(); return ValueTask.CompletedTask; }
    }
}
