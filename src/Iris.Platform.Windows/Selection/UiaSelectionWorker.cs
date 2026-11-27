using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 真实 UIA 选区读取 Worker 逻辑。
/// 必须在同一个无窗口 MTA 线程中初始化、执行与释放。
/// 纯逻辑依赖 INativeUiaAdapter，可在测试中使用 FakeNativeUiaAdapter 覆盖全部路径。
/// </summary>
internal static class UiaSelectionWorker
{
    public static UiaReplyFrame Execute(UiaRequestFrame request, INativeUiaAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);

        IUiaAutomationSession? session = null;
        IUiaElement? focused = null;
        IUiaTextPattern? pattern = null;
        IReadOnlyList<IUiaTextRange>? ranges = null;
        bool disposalFailed = false;

        try
        {
            UiaReplyFrame reply;
            try
            {
                reply = ReadSelectionCore(request, adapter, ref session, ref focused, ref pattern, ref ranges, ref disposalFailed);
            }
            catch (InvalidOperationException ex) when (ex.Message == "RANGE_COUNT_EXCEEDED")
            {
                reply = new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = request.RequestId,
                    Status = UiaSelectionStatus.TooLong,
                    ErrorCode = "RANGE_COUNT_EXCEEDED"
                };
            }
            finally
            {
                if (ranges != null)
                {
                    foreach (var r in ranges)
                    {
                        try { r.Dispose(); } catch { disposalFailed = true; }
                    }
                }
                if (pattern != null)
                {
                    try { pattern.Dispose(); } catch { disposalFailed = true; }
                }
                if (focused != null)
                {
                    try { focused.Dispose(); } catch { disposalFailed = true; }
                }
                if (session != null)
                {
                    try { session.Dispose(); } catch { disposalFailed = true; }
                }

                if (disposalFailed)
                {
                    reply = new UiaReplyFrame
                    {
                        ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                        RequestId = request.RequestId,
                        Status = UiaSelectionStatus.Failed,
                        ErrorCode = "COM_CLEANUP_FAILED"
                    };
                }
            }

            return reply;
        }
        catch (COMException ex)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Failed,
                ErrorCode = $"COM_0x{ex.HResult:X8}"
            };
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Failed,
                ErrorCode = $"COM_0x{ex.HResult:X8}"
            };
        }
        catch (Exception)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Failed,
                ErrorCode = "INTERNAL_WORKER_ERROR"
            };
        }
    }

    private static UiaReplyFrame ReadSelectionCore(
        UiaRequestFrame request,
        INativeUiaAdapter adapter,
        ref IUiaAutomationSession? session,
        ref IUiaElement? focused,
        ref IUiaTextPattern? pattern,
        ref IReadOnlyList<IUiaTextRange>? ranges,
        ref bool disposalFailed)
    {
        // 1. 验证预期前台 HWND/PID
        var fgHwnd = adapter.GetForegroundWindow();
        if (fgHwnd != (nint)request.Hwnd || adapter.GetWindowProcessId(fgHwnd) != request.ProcessId)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.TargetChanged,
                ErrorCode = "TARGET_CHANGED"
            };
        }

        // 2. 验证非 Iris 自身
        if (request.ProcessId == adapter.GetCurrentProcessId())
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.TargetChanged,
                ErrorCode = "TARGET_IS_IRIS"
            };
        }

        // 3. 在本 MTA 创建 UIA 会话
        session = adapter.CreateSession(UiaProtocolConstants.RequestTimeoutMs, UiaProtocolConstants.RequestTimeoutMs);
        focused = session.GetFocusedElement();
        if (focused == null)
        {
            if (TryReadChildWindowSelection(request, adapter, session, 0, false, ref disposalFailed, out var noFocusReply))
            {
                return noFocusReply;
            }
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Unsupported,
                ErrorCode = "NO_FOCUSED_ELEMENT"
            };
        }

        // 4. 验证 FocusedElement 所属来源
        if (!IsElementFromExpectedSource(focused, (nint)request.Hwnd, request.ProcessId, adapter, ref disposalFailed))
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Unsupported,
                ErrorCode = "ELEMENT_SOURCE_MISMATCH"
            };
        }

        // 5. 检查是否为密码/敏感控件
        if (focused.IsPassword)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Sensitive,
                ErrorCode = "PASSWORD_CONTROL"
            };
        }

        // 6. 获取文本模式 (优先 TextPattern2, 降级 TextPattern)
        pattern = focused.GetTextPattern();
        if (pattern == null || !pattern.SupportsSelection)
        {
            if (TryReadChildWindowSelection(request, adapter, session, focused.NativeWindowHandle, false, ref disposalFailed, out var noPatternReply))
            {
                return noPatternReply;
            }
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Unsupported,
                ErrorCode = "TEXT_PATTERN_UNSUPPORTED"
            };
        }

        // 7. 读取选区
        ranges = pattern.GetSelection();
        if (ranges.Count == 0)
        {
            if (TryReadChildWindowSelection(request, adapter, session, focused.NativeWindowHandle, true, ref disposalFailed, out var emptyRangesReply))
            {
                return emptyRangesReply;
            }
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Empty
            };
        }

        if (ranges.Count > UiaProtocolConstants.MaxRanges)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.TooLong,
                ErrorCode = "RANGE_COUNT_EXCEEDED"
            };
        }

        var sb = new StringBuilder();
        int totalUnits = 0;
        bool hasNonWhitespace = false;
        bool isFirstNonEmpty = true;

        for (int i = 0; i < ranges.Count; i++)
        {
            int delimiterCost = isFirstNonEmpty ? 0 : 1;
            int availableIfNonEmpty = UiaProtocolConstants.MaxTextUnits - totalUnits - delimiterCost;

            // 有界预读：空 range 不消耗分隔符预算
            int queryLimit = availableIfNonEmpty > 0 ? availableIfNonEmpty + 1 : 1;
            var text = ranges[i].GetText(queryLimit);

            if (string.IsNullOrEmpty(text))
            {
                // 空 range 不消耗分隔符预算与文本预算
                continue;
            }

            if (availableIfNonEmpty < 0 || text.Length > availableIfNonEmpty)
            {
                return new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = request.RequestId,
                    Status = UiaSelectionStatus.TooLong,
                    ErrorCode = "LENGTH_EXCEEDED"
                };
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                hasNonWhitespace = true;
            }

            if (!isFirstNonEmpty)
            {
                sb.Append('\n');
                totalUnits += 1;
            }

            sb.Append(text);
            totalUnits += text.Length;
            isFirstNonEmpty = false;

            if (totalUnits > UiaProtocolConstants.MaxTextUnits)
            {
                return new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = request.RequestId,
                    Status = UiaSelectionStatus.TooLong,
                    ErrorCode = "LENGTH_EXCEEDED"
                };
            }
        }

        if (!hasNonWhitespace || sb.Length == 0)
        {
            if (TryReadChildWindowSelection(request, adapter, session, focused.NativeWindowHandle, true, ref disposalFailed, out var emptyTextReply))
            {
                return emptyTextReply;
            }
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Empty
            };
        }

        // 8. 选区读取结束，再次核对预期前台来源，变化则丢弃文本
        var fgFinal = adapter.GetForegroundWindow();
        if (fgFinal != (nint)request.Hwnd || adapter.GetWindowProcessId(fgFinal) != request.ProcessId)
        {
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.TargetChanged,
                ErrorCode = "TARGET_CHANGED"
            };
        }

        return new UiaReplyFrame
        {
            ProtocolVersion = UiaProtocolConstants.CurrentVersion,
            RequestId = request.RequestId,
            Status = UiaSelectionStatus.Success,
            Text = sb.ToString()
        };
    }

    private static bool TryReadChildWindowSelection(
        UiaRequestFrame request,
        INativeUiaAdapter adapter,
        IUiaAutomationSession session,
        nint skipHwnd,
        bool focusedHadTextPattern,
        ref bool disposalFailed,
        out UiaReplyFrame reply)
    {
        reply = null!;
        var children = adapter.GetChildWindows((nint)request.Hwnd, request.ProcessId);
        if (children.Count == 0)
        {
            return false;
        }

        bool sawAnyTextPattern = focusedHadTextPattern;
        for (int c = 0; c < children.Count; c++)
        {
            nint childHwnd = children[c];
            if (childHwnd == 0 || childHwnd == skipHwnd) continue;

            IUiaElement? childElement = null;
            IUiaTextPattern? childPattern = null;
            IReadOnlyList<IUiaTextRange>? childRanges = null;
            try
            {
                childElement = session.ElementFromHandle(childHwnd);
                if (childElement == null) continue;
                if (childElement.ProcessId != 0 && childElement.ProcessId != request.ProcessId) continue;
                if (childElement.IsPassword)
                {
                    reply = new UiaReplyFrame
                    {
                        ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                        RequestId = request.RequestId,
                        Status = UiaSelectionStatus.Sensitive,
                        ErrorCode = "PASSWORD_CONTROL"
                    };
                    return true;
                }

                childPattern = childElement.GetTextPattern();
                if (childPattern == null || !childPattern.SupportsSelection) continue;

                sawAnyTextPattern = true;
                childRanges = childPattern.GetSelection();
                if (childRanges.Count == 0) continue;
                if (childRanges.Count > UiaProtocolConstants.MaxRanges)
                {
                    reply = new UiaReplyFrame
                    {
                        ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                        RequestId = request.RequestId,
                        Status = UiaSelectionStatus.TooLong,
                        ErrorCode = "RANGE_COUNT_EXCEEDED"
                    };
                    return true;
                }

                var sb = new StringBuilder();
                int totalUnits = 0;
                bool hasNonWhitespace = false;
                bool isFirstNonEmpty = true;
                for (int i = 0; i < childRanges.Count; i++)
                {
                    int delimiterCost = isFirstNonEmpty ? 0 : 1;
                    int availableIfNonEmpty = UiaProtocolConstants.MaxTextUnits - totalUnits - delimiterCost;
                    int queryLimit = availableIfNonEmpty > 0 ? availableIfNonEmpty + 1 : 1;
                    var text = childRanges[i].GetText(queryLimit);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (availableIfNonEmpty < 0 || text.Length > availableIfNonEmpty)
                    {
                        reply = new UiaReplyFrame
                        {
                            ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                            RequestId = request.RequestId,
                            Status = UiaSelectionStatus.TooLong,
                            ErrorCode = "LENGTH_EXCEEDED"
                        };
                        return true;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) hasNonWhitespace = true;
                    if (!isFirstNonEmpty)
                    {
                        sb.Append('\n');
                        totalUnits += 1;
                    }
                    sb.Append(text);
                    totalUnits += text.Length;
                    isFirstNonEmpty = false;
                    if (totalUnits > UiaProtocolConstants.MaxTextUnits)
                    {
                        reply = new UiaReplyFrame
                        {
                            ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                            RequestId = request.RequestId,
                            Status = UiaSelectionStatus.TooLong,
                            ErrorCode = "LENGTH_EXCEEDED"
                        };
                        return true;
                    }
                }

                if (!hasNonWhitespace || sb.Length == 0) continue;

                var fgFinal = adapter.GetForegroundWindow();
                if (fgFinal != (nint)request.Hwnd || adapter.GetWindowProcessId(fgFinal) != request.ProcessId)
                {
                    reply = new UiaReplyFrame
                    {
                        ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                        RequestId = request.RequestId,
                        Status = UiaSelectionStatus.TargetChanged,
                        ErrorCode = "TARGET_CHANGED"
                    };
                    return true;
                }

                reply = new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = request.RequestId,
                    Status = UiaSelectionStatus.Success,
                    Text = sb.ToString()
                };
                return true;
            }
            finally
            {
                if (childRanges != null)
                {
                    foreach (var r in childRanges)
                    {
                        try { r.Dispose(); } catch { disposalFailed = true; }
                    }
                }
                if (childPattern != null)
                {
                    try { childPattern.Dispose(); } catch { disposalFailed = true; }
                }
                if (childElement != null)
                {
                    try { childElement.Dispose(); } catch { disposalFailed = true; }
                }
            }
        }

        if (sawAnyTextPattern && !focusedHadTextPattern)
        {
            reply = new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Empty
            };
            return true;
        }

        return false;
    }

    private static bool IsElementFromExpectedSource(
        IUiaElement element,
        nint expectedHwnd,
        uint expectedPid,
        INativeUiaAdapter adapter,
        ref bool disposalFailed)
    {
        // A missing provider PID requires affirmative native ownership; a conflicting PID is never overridden.
        if (element.ProcessId != 0 && element.ProcessId != expectedPid)
        {
            return false;
        }

        // 拥有直接原生 HWND 时，验证其是否为目标窗口或其子窗口
        if (element.NativeWindowHandle != 0)
        {
            return HasNativeWindowSource(element.NativeWindowHandle, expectedHwnd, expectedPid, adapter);
        }

        // 虚拟元素（无原生 HWND），有界遍历祖先（深度上限 32）寻找关联的原生窗口
        var ancestors = new List<IUiaElement>();
        try
        {
            var current = element;
            for (int depth = 0; depth < 32; depth++)
            {
                var parent = current.GetParent(1);
                if (parent == null)
                {
                    break;
                }

                ancestors.Add(parent);
                if (parent.ProcessId != 0 && parent.ProcessId != expectedPid) return false;
                if (parent.NativeWindowHandle != 0)
                {
                    return HasNativeWindowSource(parent.NativeWindowHandle, expectedHwnd, expectedPid, adapter);
                }

                current = parent;
            }

            return false;
        }
        finally
        {
            foreach (var a in ancestors)
            {
                try
                {
                    a.Dispose();
                }
                catch
                {
                    disposalFailed = true;
                }
            }
        }
    }

    private static bool HasNativeWindowSource(nint window, nint expectedWindow, uint expectedPid, INativeUiaAdapter adapter) =>
        adapter.GetWindowProcessId(window) == expectedPid && adapter.IsWindowOrAncestor(window, expectedWindow);
}
