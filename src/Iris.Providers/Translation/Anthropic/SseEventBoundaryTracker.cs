using System;

namespace Iris.Providers.Translation.Anthropic;

/// <summary>
/// 针对 SSE 流的有界事件边界检测器（按完整事件续期，支持 LF/CRLF/CR、多字节 UTF-8 跨 chunk、有界状态）。
/// </summary>
internal sealed class SseEventBoundaryTracker
{
    private readonly Action _onEventBoundary;
    private bool _pendingCr;
    private bool _lineHasChars;
    private int _linePrefixLen;
    private readonly byte[] _linePrefix = new byte[32];
    private bool _eventHasData;
    private bool _eventHasPing;

    public SseEventBoundaryTracker(Action onEventBoundary)
    {
        _onEventBoundary = onEventBoundary ?? throw new ArgumentNullException(nameof(onEventBoundary));
    }

    public void ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];

            if (b == (byte)'\r')
            {
                if (_pendingCr)
                {
                    // 上一个字节也是 CR（未跟 LF），表示前一行已由 CR 结束。
                    // 当前又读到一个 CR，表示新的一行是空行，触发 EndLine()。
                    EndLine();
                }
                else
                {
                    // CR 结束当前行内容
                    EndLine();
                    _pendingCr = true;
                }
            }
            else if (b == (byte)'\n')
            {
                if (_pendingCr)
                {
                    // 紧随 CR 之后的 LF：属于同一个 CRLF 换行符，消费掉即可。
                    _pendingCr = false;
                }
                else
                {
                    // 独立的 LF 结束当前行
                    EndLine();
                }
            }
            else
            {
                if (_pendingCr)
                {
                    // 上一个 CR 是独立换行（后续字符不是 LF）
                    _pendingCr = false;
                }

                _lineHasChars = true;
                if (_linePrefixLen < _linePrefix.Length)
                {
                    _linePrefix[_linePrefixLen++] = b;
                }
            }
        }
    }

    private void EndLine()
    {
        if (!_lineHasChars)
        {
            // 空行：标志着当前 SSE 事件的结束
            if (_eventHasData || _eventHasPing)
            {
                _onEventBoundary();
            }

            _eventHasData = false;
            _eventHasPing = false;
        }
        else
        {
            // 非空行：检查行前缀
            InspectLinePrefix(_linePrefix.AsSpan(0, _linePrefixLen));
            _lineHasChars = false;
            _linePrefixLen = 0;
        }
    }

    private void InspectLinePrefix(ReadOnlySpan<byte> prefix)
    {
        // 检查 data: 前缀
        if (prefix.StartsWith("data:"u8) || prefix.StartsWith("data "u8) || prefix.SequenceEqual("data"u8))
        {
            _eventHasData = true;
            return;
        }

        // 检查 event: ping
        if (prefix.StartsWith("event:"u8))
        {
            var value = prefix["event:".Length..];
            while (value.Length > 0 && (value[0] == (byte)' ' || value[0] == (byte)'\t'))
            {
                value = value[1..];
            }
            while (value.Length > 0 && (value[^1] == (byte)' ' || value[^1] == (byte)'\t'))
            {
                value = value[..^1];
            }

            if (value.SequenceEqual("ping"u8))
            {
                _eventHasPing = true;
            }
        }
    }
}
