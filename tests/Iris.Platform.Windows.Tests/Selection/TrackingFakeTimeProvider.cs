using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace Iris.Platform.Windows.Tests.Selection;

/// <summary>
/// 包装 FakeTimeProvider 并提供精确的计时器创建/进入通知，
/// 确保测试在推进虚拟时间前，被测逻辑已真正进入计时器等待，消除时间竞态。
/// </summary>
internal sealed class TrackingFakeTimeProvider : TimeProvider, IDisposable
{
    private readonly FakeTimeProvider _inner;
    private readonly SemaphoreSlim _timerSignal = new(0);

    public void Dispose()
    {
        _timerSignal.Dispose();
    }

    public TrackingFakeTimeProvider(FakeTimeProvider? inner = null)
    {
        _inner = inner ?? new FakeTimeProvider();
    }

    public FakeTimeProvider Inner => _inner;

    /// <summary>
    /// 等待被测代码进入 Task.Delay / CreateTimer。
    /// 基于 SemaphoreSlim，即使 CreateTimer 先于 Wait 调用也不会丢失信号。
    /// </summary>
    public Task WaitForTimerCreatedAsync(CancellationToken ct = default)
    {
        return _timerSignal.WaitAsync(ct);
    }

    /// <summary>
    /// 推进虚拟时钟。
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        _inner.Advance(delta);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _inner.CreateTimer(callback, state, dueTime, period);
        _timerSignal.Release();
        return timer;
    }

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override long TimestampFrequency => _inner.TimestampFrequency;
}
