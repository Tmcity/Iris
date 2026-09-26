using System;
using System.Net.Http;

namespace Iris.Providers.Http;

/// <summary>
/// 计算 LLM 请求重试延迟的纯逻辑。
/// </summary>
internal static class LlmRetryDelay
{
    public static TimeSpan Compute(HttpResponseMessage? response, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var retryAfter = response?.Headers?.RetryAfter;
        if (retryAfter != null)
        {
            if (retryAfter.Delta.HasValue)
            {
                var seconds = Math.Clamp(retryAfter.Delta.Value.TotalSeconds, 0.0, 5.0);
                return TimeSpan.FromSeconds(seconds);
            }

            if (retryAfter.Date.HasValue)
            {
                var diff = retryAfter.Date.Value - timeProvider.GetUtcNow();
                var seconds = Math.Clamp(diff.TotalSeconds, 0.0, 5.0);
                return TimeSpan.FromSeconds(seconds);
            }
        }

        var randomSeconds = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(randomSeconds);
    }
}
