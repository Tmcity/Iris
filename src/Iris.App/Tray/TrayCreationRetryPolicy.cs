using System;

namespace Iris.App.Tray;

/// <summary>
/// 系统托盘图标创建重试策略（纯逻辑）。
/// </summary>
internal static class TrayCreationRetryPolicy
{
    public const int ErrorLogThreshold = 30;

    /// <summary>
    /// 根据已连续失败的尝试次数计算下次重试延迟。
    /// 前 30 次每 2 秒重试一次；第 31 次起每 30 秒重试一次。
    /// </summary>
    public static TimeSpan GetDelay(int failedAttempts)
    {
        if (failedAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedAttempts),
                failedAttempts,
                "Failed attempts must be greater than 0.");
        }

        return failedAttempts <= ErrorLogThreshold
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(30);
    }
}
