using System;

namespace Iris.Providers.Translation.Anthropic;

/// <summary>
/// 针对 SSE 流的有界事件边界检测器（按规范仅含 data 的完整事件续期，支持 LF/CRLF/CR、多字节 UTF-8 跨 chunk、流首 UTF-8 BOM 过滤、有界状态）。
/// </summary>
internal sealed class SseEventBoundaryTracker
{
    private readonly Action _onEventBoundary;
    private int _bomState; // 0: initial, 1: saw 0xEF, 2: saw 0xBB, -1: BOM check finished
    private bool _pendingCr;
    private bool _lineHasChars;
    private int _linePrefixLen;
    private int _lineTotalLen;
    private readonly byte[] _linePrefix = new byte[32];
    private bool _eventHasData;

    public SseEventBoundaryTracker(Action onEventBoundary)
    {
        _onEventBoundary = onEventBoundary ?? throw new ArgumentNullException(nameof(onEventBoundary));
    }

    public void ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];

            if (_bomState >= 0)
            {
                if (_bomState == 0)
                {
                    if (b == 0xEF)
                    {
                        _bomState = 1;
                        continue;
                    }
                    _bomState = -1;
                }
                else if (_bomState == 1)
                {
                    if (b == 0xBB)
                    {
                        _bomState = 2;
                        continue;
                    }
                    _bomState = -1;
                    ProcessChar(0xEF);
                }
                else if (_bomState == 2)
                {
                    if (b == 0xBF)
                    {
                        _bomState = -1;
                        continue; // 流首 UTF-8 BOM 过滤完成
                    }
                    _bomState = -1;
                    ProcessChar(0xEF);
                    ProcessChar(0xBB);
                }
            }

            ProcessChar(b);
        }
    }

    private void ProcessChar(byte b)
    {
        if (b == (byte)'\r')
        {
            if (_pendingCr)
            {
                // 连续 CR
                EndLine();
            }
            else
            {
                EndLine();
                _pendingCr = true;
            }
        }
        else if (b == (byte)'\n')
        {
            if (_pendingCr)
            {
                // CRLF 中的 LF
                _pendingCr = false;
            }
            else
            {
                // 单独的 LF
                EndLine();
            }
        }
        else
        {
            if (_pendingCr)
            {
                // 上一个 CR 是独立换行符
                _pendingCr = false;
            }

            _lineHasChars = true;
            if (_linePrefixLen < _linePrefix.Length)
            {
                _linePrefix[_linePrefixLen++] = b;
            }
            _lineTotalLen++;
        }
    }

    private void EndLine()
    {
        if (!_lineHasChars)
        {
            // 空行：标志着当前 SSE 事件的结束。仅当当前事件包含有效 data 字段时派发事件续期。
            if (_eventHasData)
            {
                _onEventBoundary();
                _eventHasData = false;
            }
        }
        else
        {
            // 非空行：检查行前缀是否为合法的 data 字段
            InspectLinePrefix();
            _lineHasChars = false;
            _linePrefixLen = 0;
            _lineTotalLen = 0;
        }
    }

    private void InspectLinePrefix()
    {
        var prefix = _linePrefix.AsSpan(0, _linePrefixLen);
        // SSE 规范：以 "data:" 开头，或整行恰好为 "data"（无冒号，空值）
        if (prefix.StartsWith("data:"u8) || (_lineTotalLen == 4 && prefix.SequenceEqual("data"u8)))
        {
            _eventHasData = true;
        }
    }
}
