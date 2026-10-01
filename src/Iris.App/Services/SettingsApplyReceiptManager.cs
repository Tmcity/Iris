using System;
using System.Collections.Generic;
using System.Threading;
using Iris.Core.Settings;

namespace Iris.App.Services;

/// <summary>
/// 待应用回执安全摘要条目。仅持有 opaque 回执、创建时间与安全错误消息；
/// 不持有原 plan、凭据、原 JSON、Provider、UI 或租约。
/// </summary>
internal sealed record PendingReceiptEntry(
    ISettingsApplyReceipt Receipt,
    DateTimeOffset CreatedAt,
    string? ErrorMessage);

/// <summary>
/// 进程级待应用状态管理端口（App 单例）。
/// 完整规则见 specs/PROVIDER_SETTINGS_UI.md §6。
/// </summary>
internal interface ISettingsApplyReceiptManager
{
    int MaxCapacity { get; }
    int PendingCount { get; }
    int InFlightReservations { get; }
    bool HasPendingReceipts { get; }
    IReadOnlyList<PendingReceiptEntry> GetPendingReceipts();

    /// <summary>
    /// 原子预留一个名额（用于有 Provider 或排序变更的保存计划）。
    /// 如果已达到容量上限（32），返回 false。
    /// </summary>
    bool TryReserveSlot();

    /// <summary>
    /// 释放预留的名额（在 Applied、Uncommitted 或提交前取消时调用）。
    /// </summary>
    void ReleaseReservation();

    /// <summary>
    /// 将已预留的名额转换为已保存的待应用回执（在 Core 返回 ApplyPending 时调用）。
    /// </summary>
    void ConvertReservationToPending(ISettingsApplyReceipt receipt, string? errorMessage);

    /// <summary>
    /// 当 RetryApplyAsync 成功（IsComplete == true）时移除回执。
    /// </summary>
    bool RemoveReceipt(ISettingsApplyReceipt receipt);

    /// <summary>
    /// 更新回执的最新安全错误信息（重试失败但未完成时）。
    /// </summary>
    void UpdateReceiptError(ISettingsApplyReceipt receipt, string? errorMessage);

    /// <summary>
    /// 状态发生变化时触发（新增回执、移除回执、错误更新等）。
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>
/// 纯内存单例状态服务，持有未完成 ISettingsApplyReceipt 和安全结果摘要。
/// 构造无 I/O、无网络重试；容量固定为 32。
/// </summary>
internal sealed class SettingsApplyReceiptManager : ISettingsApplyReceiptManager
{
    public const int MaxQuota = 32;

    private readonly Lock _lock = new();
    private readonly List<PendingReceiptEntry> _receipts = [];
    private int _inFlightReservations;

    public int MaxCapacity => MaxQuota;

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _receipts.Count;
            }
        }
    }

    public int InFlightReservations
    {
        get
        {
            lock (_lock)
            {
                return _inFlightReservations;
            }
        }
    }

    public bool HasPendingReceipts
    {
        get
        {
            lock (_lock)
            {
                return _receipts.Count > 0;
            }
        }
    }

    public IReadOnlyList<PendingReceiptEntry> GetPendingReceipts()
    {
        lock (_lock)
        {
            return _receipts.ToArray();
        }
    }

    public bool TryReserveSlot()
    {
        lock (_lock)
        {
            if (_receipts.Count + _inFlightReservations >= MaxQuota)
            {
                return false;
            }
            _inFlightReservations++;
            return true;
        }
    }

    public void ReleaseReservation()
    {
        lock (_lock)
        {
            if (_inFlightReservations > 0)
            {
                _inFlightReservations--;
            }
        }
    }

    public void ConvertReservationToPending(ISettingsApplyReceipt receipt, string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        EventHandler? handler = null;
        lock (_lock)
        {
            if (_inFlightReservations > 0)
            {
                _inFlightReservations--;
            }

            var existingIndex = _receipts.FindIndex(r => r.Receipt.CommitId == receipt.CommitId);
            if (existingIndex >= 0)
            {
                _receipts[existingIndex] = new PendingReceiptEntry(receipt, _receipts[existingIndex].CreatedAt, errorMessage);
            }
            else
            {
                _receipts.Add(new PendingReceiptEntry(receipt, DateTimeOffset.UtcNow, errorMessage));
            }
            handler = Changed;
        }
        handler?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveReceipt(ISettingsApplyReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        bool removed = false;
        EventHandler? handler = null;
        lock (_lock)
        {
            var idx = _receipts.FindIndex(r => r.Receipt.CommitId == receipt.CommitId);
            if (idx >= 0)
            {
                _receipts.RemoveAt(idx);
                removed = true;
                handler = Changed;
            }
        }
        if (removed)
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    public void UpdateReceiptError(ISettingsApplyReceipt receipt, string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        EventHandler? handler = null;
        lock (_lock)
        {
            var idx = _receipts.FindIndex(r => r.Receipt.CommitId == receipt.CommitId);
            if (idx >= 0)
            {
                _receipts[idx] = new PendingReceiptEntry(receipt, _receipts[idx].CreatedAt, errorMessage);
                handler = Changed;
            }
        }
        handler?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
}
