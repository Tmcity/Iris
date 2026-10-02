using System.Threading;

namespace Iris.Platform.Windows.Hotkey;

/// <summary>
/// 热键 ID 分配器接口。
/// HOTKEY_SERVICE.md §3 要求：每次有效注册分配新 ID，区间 1..0xBFFF (49151)，进程内不复用已消耗 ID。
/// </summary>
internal interface IHotkeyIdAllocator
{
    /// <summary>
    /// 分配下一个单调递增 ID；耗尽时返回 -1 或 0。
    /// </summary>
    int AllocateId();
}

/// <summary>
/// 默认进程级热键 ID 分配器，使用全局原子递增计数器。
/// 跨 HotkeyService 实例保证进程内不复用已消耗 ID。
/// </summary>
internal sealed class DefaultHotkeyIdAllocator : IHotkeyIdAllocator
{
    public const int MaxId = 0xBFFF;
    public static readonly DefaultHotkeyIdAllocator Instance = new();

    private static int s_currentId;

    public int AllocateId()
    {
        int next = Interlocked.Increment(ref s_currentId);
        if (next > MaxId)
        {
            return -1;
        }
        return next;
    }
}
