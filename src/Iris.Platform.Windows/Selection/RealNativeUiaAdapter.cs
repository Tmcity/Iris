using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace Iris.Platform.Windows.Selection;

internal sealed unsafe class RealNativeUiaAdapter : INativeUiaAdapter
{
    public static readonly RealNativeUiaAdapter Instance = new();

    public nint GetForegroundWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        return (nint)hwnd.Value;
    }

    public uint GetWindowProcessId(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        uint pid = 0;
        uint threadId = PInvoke.GetWindowThreadProcessId(new HWND((void*)hwnd), &pid);
        return threadId != 0 ? pid : 0;
    }

    public bool IsWindowValid(nint hwnd)
    {
        return hwnd != 0 && PInvoke.IsWindow(new HWND((void*)hwnd));
    }

    public bool IsWindowOrAncestor(nint childHwnd, nint expectedAncestorHwnd)
    {
        if (childHwnd == 0 || expectedAncestorHwnd == 0)
        {
            return false;
        }

        if (childHwnd == expectedAncestorHwnd)
        {
            return true;
        }

        return PInvoke.IsChild(new HWND((void*)expectedAncestorHwnd), new HWND((void*)childHwnd));
    }

    public uint GetCurrentProcessId()
    {
        return PInvoke.GetCurrentProcessId();
    }

    public IUiaAutomationSession CreateSession(int connectionTimeoutMs, int transactionTimeoutMs)
    {
        object? comObj = null;

        // 优先使用 CUIAutomation8 / IUIAutomation2 以便配置 300ms 事务与连接超时
        try
        {
            Guid clsid8 = typeof(CUIAutomation8).GUID;
            Guid iid2 = typeof(IUIAutomation2).GUID;
            var hr = PInvoke.CoCreateInstance(&clsid8, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid2, out var obj);
            if (!hr.Failed && obj != null)
            {
                comObj = obj;
            }
        }
        catch
        {
            comObj = null;
        }

        // 回退至 CUIAutomation / IUIAutomation
        if (comObj == null)
        {
            Guid clsid = typeof(CUIAutomation).GUID;
            Guid iid = typeof(IUIAutomation).GUID;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, out comObj).ThrowOnFailure();
        }

        if (comObj is IUIAutomation2 uia2)
        {
            uia2.ConnectionTimeout = (uint)connectionTimeoutMs;
            uia2.TransactionTimeout = (uint)transactionTimeoutMs;
        }

        return new RealUiaAutomationSession((IUIAutomation)comObj!);
    }
}

internal sealed class RealUiaAutomationSession : IUiaAutomationSession
{
    private readonly IUIAutomation _uia;
    private bool _disposed;

    public RealUiaAutomationSession(IUIAutomation uia)
    {
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
    }

    public IUiaElement? GetFocusedElement()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var element = _uia.GetFocusedElement();
            return element != null ? new RealUiaElement(element, _uia) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Marshal.ReleaseComObject(_uia);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class RealUiaElement : IUiaElement
{
    private readonly IUIAutomationElement _element;
    private readonly IUIAutomation _uia;
    private bool _disposed;

    public RealUiaElement(IUIAutomationElement element, IUIAutomation uia)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
    }

    public unsafe uint ProcessId => (uint)_element.CurrentProcessId;
    public unsafe nint NativeWindowHandle => (nint)_element.CurrentNativeWindowHandle.Value;
    public bool IsPassword => _element.CurrentIsPassword != 0;

    public IUiaElement? GetParent(int maxDepth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var walker = _uia.ControlViewWalker;
            var parent = walker?.GetParentElement(_element);
            return parent != null ? new RealUiaElement(parent, _uia) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public IUiaTextPattern? GetTextPattern()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            // 优先 TextPattern2
            var patternObj = _element.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPattern2Id);
            if (patternObj == null)
            {
                patternObj = _element.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPatternId);
            }

            if (patternObj is IUIAutomationTextPattern pattern)
            {
                return new RealUiaTextPattern(pattern);
            }

            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Marshal.ReleaseComObject(_element);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class RealUiaTextPattern : IUiaTextPattern
{
    private readonly IUIAutomationTextPattern _pattern;
    private bool _disposed;

    public RealUiaTextPattern(IUIAutomationTextPattern pattern)
    {
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
    }

    public bool SupportsSelection
    {
        get
        {
            try
            {
                return _pattern.SupportedTextSelection != SupportedTextSelection.SupportedTextSelection_None;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<IUiaTextRange> GetSelection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var arr = _pattern.GetSelection();
            if (arr == null)
            {
                return [];
            }

            int len = arr.Length;
            var list = new List<IUiaTextRange>(len);
            for (int i = 0; i < len; i++)
            {
                var r = arr.GetElement(i);
                if (r != null)
                {
                    list.Add(new RealUiaTextRange(r));
                }
            }

            Marshal.ReleaseComObject(arr);
            return list;
        }
        catch (COMException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Marshal.ReleaseComObject(_pattern);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class RealUiaTextRange : IUiaTextRange
{
    private readonly IUIAutomationTextRange _range;
    private bool _disposed;

    public RealUiaTextRange(IUIAutomationTextRange range)
    {
        _range = range ?? throw new ArgumentNullException(nameof(range));
    }

    public string? GetText(int maxLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var bstr = _range.GetText(maxLength);
            return bstr.ToString();
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Marshal.ReleaseComObject(_range);
        }
        catch
        {
            // ignore
        }
    }
}
