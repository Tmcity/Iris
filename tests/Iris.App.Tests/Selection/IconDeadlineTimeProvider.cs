using System.Collections.Concurrent;

namespace Iris.App.Tests.Selection;

// Only the external timer boundary is decorated; Controller still owns all retirement/retry decisions.
internal sealed class IconDeadlineTimeProvider(TimeProvider inner) : TimeProvider
{
    internal ConcurrentQueue<DeadlineTimer> Deadlines { get; } = new();
    internal TaskCompletionSource SecondCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? DisposeGate { get; set; }
    internal int DisposeFailures;
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override long GetTimestamp() => inner.GetTimestamp();
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        if (dueTime != TimeSpan.FromMilliseconds(3000)) return timer;
        var deadline = new DeadlineTimer(timer, this); Deadlines.Enqueue(deadline);
        if (Deadlines.Count == 2) SecondCreated.TrySetResult();
        return deadline;
    }
    internal sealed class DeadlineTimer(ITimer timer, IconDeadlineTimeProvider owner) : ITimer
    {
        internal int DisposeAttempts { get; private set; }
        internal bool IsDisposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
        public void Dispose() { timer.Dispose(); IsDisposed = true; }
        public async ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            owner.DisposeEntered.TrySetResult();
            if (owner.DisposeGate is { } gate) await gate;
            if (owner.DisposeFailures > 0) { owner.DisposeFailures--; throw new IOException("private timer failure"); }
            await timer.DisposeAsync(); IsDisposed = true;
        }
    }
}
