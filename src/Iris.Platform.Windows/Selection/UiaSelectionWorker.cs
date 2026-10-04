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
                reply = ReadSelectionCore(request, adapter, ref session, ref focused, ref pattern, ref ranges);
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
        ref IReadOnlyList<IUiaTextRange>? ranges)
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
            return new UiaReplyFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                RequestId = request.RequestId,
                Status = UiaSelectionStatus.Unsupported,
                ErrorCode = "NO_FOCUSED_ELEMENT"
            };
        }

        // 4. 验证 FocusedElement 所属来源
        if (!IsElementFromExpectedSource(focused, (nint)request.Hwnd, request.ProcessId, adapter))
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
            int remainingForText = UiaProtocolConstants.MaxTextUnits - totalUnits - delimiterCost;
            if (remainingForText <= 0)
            {
                return new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = request.RequestId,
                    Status = UiaSelectionStatus.TooLong,
                    ErrorCode = "LENGTH_EXCEEDED"
                };
            }

            var text = ranges[i].GetText(remainingForText + 1);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (text.Length > remainingForText)
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

    private static bool IsElementFromExpectedSource(IUiaElement element, nint expectedHwnd, uint expectedPid, INativeUiaAdapter adapter)
    {
        // 进程 ID 必须匹配
        if (element.ProcessId != expectedPid)
        {
            return false;
        }

        // 拥有直接原生 HWND 时，验证其是否为目标窗口或其子窗口
        if (element.NativeWindowHandle != 0)
        {
            return adapter.IsWindowOrAncestor(element.NativeWindowHandle, expectedHwnd);
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
                if (parent.ProcessId == expectedPid && parent.NativeWindowHandle != 0)
                {
                    return adapter.IsWindowOrAncestor(parent.NativeWindowHandle, expectedHwnd);
                }

                current = parent;
            }

            return false;
        }
        finally
        {
            foreach (var a in ancestors)
            {
                a.Dispose();
            }
        }
    }
}
