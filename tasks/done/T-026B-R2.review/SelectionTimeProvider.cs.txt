using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Iris.Platform.Windows.Tests.Selection;

internal sealed class SelectionTimeProvider : TimeProvider, IDisposable
{
    private readonly FakeTimeProvider _inner = new();
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _inner.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }
    public async Task WaitForDelayAsync(int milliseconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await _timers.Reader.ReadAsync(timeout.Token) != TimeSpan.FromMilliseconds(milliseconds)) { }
    }
    public void Advance(TimeSpan amount) => _inner.Advance(amount);
    public void Dispose() => _timers.Writer.TryComplete();
}
