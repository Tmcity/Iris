using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Settings;

namespace Iris.App.Hosting;

/// <summary>
/// 快捷键运行时的单个动作状态。
/// </summary>
internal enum HotkeyRuntimeActionStatus
{
    Disabled,
    Applying,
    Registered,
    Paused,
    Conflict,
    Invalid,
    Failed
}

/// <summary>
/// 快捷键运行时的单个动作状态项（不可变快照）。
/// </summary>
internal sealed record HotkeyRuntimeItemState(
    IrisAction Action,
    HotkeyBinding? TargetBinding,
    HotkeyRuntimeActionStatus Status,
    string? KeyComboText,
    string? Message,
    long ConfigurationGeneration
);

/// <summary>
/// 快捷键运行时的全局不可变状态快照。
/// </summary>
internal sealed class HotkeyRuntimeSnapshot
{
    public long SnapshotId { get; }
    public bool IsPaused { get; }
    public IReadOnlyDictionary<IrisAction, HotkeyRuntimeItemState> Items { get; }

    public HotkeyRuntimeSnapshot(long snapshotId, bool isPaused, IReadOnlyDictionary<IrisAction, HotkeyRuntimeItemState> items)
    {
        SnapshotId = snapshotId;
        IsPaused = isPaused;
        Items = items ?? new Dictionary<IrisAction, HotkeyRuntimeItemState>();
    }

    public bool HasAnyFailure => Items.Values.Any(item =>
        item.Status is HotkeyRuntimeActionStatus.Conflict
                     or HotkeyRuntimeActionStatus.Failed
                     or HotkeyRuntimeActionStatus.Invalid);
}

/// <summary>
/// 快捷键暂停租约接口。
/// </summary>
internal interface IHotkeyPauseLease : IAsyncDisposable
{
    Guid LeaseId { get; }
    string Owner { get; }
}
