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

        try
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
            using var session = adapter.CreateSession(UiaProtocolConstants.RequestTimeoutMs, UiaProtocolConstants.RequestTimeoutMs);
            using var focused = session.GetFocusedElement();
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
            using var pattern = focused.GetTextPattern();
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
            var ranges = pattern.GetSelection();
            try
            {
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

                for (int i = 0; i < ranges.Count; i++)
                {
                    if (i > 0)
                    {
                        if (totalUnits + 1 > UiaProtocolConstants.MaxTextUnits)
                        {
                            return new UiaReplyFrame
                            {
                                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                                RequestId = request.RequestId,
                                Status = UiaSelectionStatus.TooLong,
                                ErrorCode = "LENGTH_EXCEEDED"
                            };
                        }
                        totalUnits += 1;
                    }

                    int remaining = UiaProtocolConstants.MaxTextUnits - totalUnits;
                    if (remaining <= 0)
                    {
                        return new UiaReplyFrame
                        {
                            ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                            RequestId = request.RequestId,
                            Status = UiaSelectionStatus.TooLong,
                            ErrorCode = "LENGTH_EXCEEDED"
                        };
                    }

                    var text = ranges[i].GetText(remaining + 1);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    if (text.Length > remaining)
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

                    totalUnits += text.Length;
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

                    if (i > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(text);
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
            finally
            {
                foreach (var r in ranges)
                {
                    r.Dispose();
                }
            }
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
