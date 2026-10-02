using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.App.Translation;

internal readonly record struct QueuedDelta(
    Guid OperationId,
    string ProviderId,
    Guid RequestId,
    string Delta);

/// <summary>
/// 全窗口共享的增量缓冲队列与预算管理。
/// 限制最多 262,144 个未送入 UI 的字符，超出时产生背压。
/// 完整预算与刷新规则见 TRANSLATION_WINDOW.md §5。
/// </summary>
internal sealed class TranslationDeltaBuffer
{
    public const int MaxBufferedChars = 262_144;
    private readonly Lock _gate = new();
    private readonly Queue<QueuedDelta> _queue = new();
    private int _bufferedChars;
    private TaskCompletionSource? _capacityWaitTcs;
    private bool _isDisposed;

    public int BufferedChars
    {
        get
        {
            lock (_gate)
            {
                return _bufferedChars;
            }
        }
    }

    public async Task EnqueueAsync(QueuedDelta item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Delta))
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? waitTask = null;
            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                if (_bufferedChars + item.Delta.Length <= MaxBufferedChars)
                {
                    _queue.Enqueue(item);
                    _bufferedChars += item.Delta.Length;
                    return;
                }

                _capacityWaitTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _capacityWaitTcs.Task;
            }

            if (waitTask != null)
            {
                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyList<QueuedDelta> Drain()
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return [];
            }

            var items = _queue.ToArray();
            _queue.Clear();
            _bufferedChars = 0;

            if (_capacityWaitTcs != null)
            {
                var tcs = _capacityWaitTcs;
                _capacityWaitTcs = null;
                tcs.TrySetResult();
            }

            return items;
        }
    }

    public void DiscardForCard(Guid operationId, string providerId, Guid requestId)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            var items = _queue.ToArray();
            _queue.Clear();
            _bufferedChars = 0;

            foreach (var item in items)
            {
                if (item.OperationId == operationId &&
                    item.ProviderId == providerId &&
                    item.RequestId == requestId)
                {
                    continue;
                }

                _queue.Enqueue(item);
                _bufferedChars += item.Delta.Length;
            }

            if (_capacityWaitTcs != null && _bufferedChars < MaxBufferedChars)
            {
                var tcs = _capacityWaitTcs;
                _capacityWaitTcs = null;
                tcs.TrySetResult();
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _queue.Clear();
            _bufferedChars = 0;
            if (_capacityWaitTcs != null)
            {
                var tcs = _capacityWaitTcs;
                _capacityWaitTcs = null;
                tcs.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _isDisposed = true;
            _queue.Clear();
            _bufferedChars = 0;
            if (_capacityWaitTcs != null)
            {
                var tcs = _capacityWaitTcs;
                _capacityWaitTcs = null;
                tcs.TrySetCanceled();
            }
        }
    }
}

internal static class TextBudgetManager
{
    public const int MaxCardChars = 1_048_576; // 1M UTF-16 code units
    public const int MaxTotalChars = 4_194_304; // 4M UTF-16 code units

    public static string TruncateSurrogateSafe(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (char.IsHighSurrogate(text[maxChars - 1]))
        {
            maxChars--;
        }

        return text[..maxChars];
    }
}
