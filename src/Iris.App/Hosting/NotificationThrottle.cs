using System;

namespace Iris.App.Hosting;

/// <summary>
/// 通知限流器：每 10 秒最多允许一次通知，避免高频异常刷屏。
/// </summary>
internal sealed class NotificationThrottle
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private DateTimeOffset _lastAcquired = DateTimeOffset.MinValue;

    public NotificationThrottle(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _lastAcquired >= Interval)
            {
                _lastAcquired = now;
                return true;
            }

            return false;
        }
    }
}
