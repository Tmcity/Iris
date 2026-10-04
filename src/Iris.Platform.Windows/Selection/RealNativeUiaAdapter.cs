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
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            throw new InvalidOperationException("RealNativeUiaAdapter must be executed on an MTA thread.");
        }

        var hrCoInit = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (hrCoInit.Failed && hrCoInit.Value != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE
        {
            hrCoInit.ThrowOnFailure();
        }
        bool coInitSucceeded = !hrCoInit.Failed;
        int ownerThreadId = Environment.CurrentManagedThreadId;

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
            try
            {
                Guid clsid = typeof(CUIAutomation).GUID;
                Guid iid = typeof(IUIAutomation).GUID;
                PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, out comObj).ThrowOnFailure();
            }
            catch
            {
                if (coInitSucceeded)
                {
                    try { PInvoke.CoUninitialize(); } catch { }
                }
                throw;
            }
        }

        try
        {
            if (comObj is IUIAutomation2 uia2)
            {
                uia2.ConnectionTimeout = (uint)connectionTimeoutMs;
                uia2.TransactionTimeout = (uint)transactionTimeoutMs;
            }
        }
        catch
        {
            try { Marshal.ReleaseComObject(comObj); } catch { }
            if (coInitSucceeded)
            {
                try { PInvoke.CoUninitialize(); } catch { }
            }
            throw;
        }

        return new RealUiaAutomationSession((IUIAutomation)comObj!, ownerThreadId, coInitSucceeded);
    }
}

internal sealed class RealUiaAutomationSession : IUiaAutomationSession
{
    private readonly IUIAutomation _uia;
    private readonly int _ownerThreadId;
    private readonly bool _coInitSucceeded;
    private bool _disposed;

    public RealUiaAutomationSession(IUIAutomation uia, int ownerThreadId = 0, bool coInitSucceeded = false)
    {
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
        _ownerThreadId = ownerThreadId != 0 ? ownerThreadId : Environment.CurrentManagedThreadId;
        _coInitSucceeded = coInitSucceeded;
    }

    public IUiaElement? GetFocusedElement()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var element = _uia.GetFocusedElement();
        return element != null ? new RealUiaElement(element, _uia) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("RealUiaAutomationSession must be disposed on the same thread it was created on.");
        }

        Exception? releaseEx = null;
        try
        {
            if (Marshal.IsComObject(_uia))
            {
                Marshal.ReleaseComObject(_uia);
            }
        }
        catch (Exception ex)
        {
            releaseEx = ex;
        }

        if (_coInitSucceeded)
        {
            try
            {
                PInvoke.CoUninitialize();
            }
            catch (Exception ex)
            {
                releaseEx ??= ex;
            }
        }

        if (releaseEx != null)
        {
            throw releaseEx;
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
        IUIAutomationTreeWalker? walker = null;
        try
        {
            walker = _uia.ControlViewWalker;
            var parent = walker?.GetParentElement(_element);
            return parent != null ? new RealUiaElement(parent, _uia) : null;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80040201)) // UIA_E_ELEMENTNOTAVAILABLE
        {
            return null;
        }
        finally
        {
            if (walker != null && Marshal.IsComObject(walker))
            {
                Marshal.ReleaseComObject(walker);
            }
        }
    }

    public IUiaTextPattern? GetTextPattern()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        object? patternObj = null;

        // 优先 TextPattern2：仅在显式不受支持时降级，断连等其它错误不可吞没
        try
        {
            patternObj = _element.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPattern2Id);
        }
        catch (COMException ex) when (IsPatternNotSupported(ex))
        {
            patternObj = null;
        }

        // 降级经典 TextPattern
        if (patternObj == null)
        {
            try
            {
                patternObj = _element.GetCurrentPattern((UIA_PATTERN_ID)UIA_PATTERN_ID.UIA_TextPatternId);
            }
            catch (COMException ex) when (IsPatternNotSupported(ex))
            {
                patternObj = null;
            }
        }

        if (patternObj is IUIAutomationTextPattern pattern)
        {
            return new RealUiaTextPattern(pattern);
        }

        if (patternObj != null && Marshal.IsComObject(patternObj))
        {
            Marshal.ReleaseComObject(patternObj);
        }

        return null;
    }

    private static bool IsPatternNotSupported(COMException ex)
    {
        const int UIA_E_NOTSUPPORTED = unchecked((int)0x80040204);
        const int E_NOTIMPL = unchecked((int)0x80004001);
        const int E_NOINTERFACE = unchecked((int)0x80004002);
        return ex.HResult == UIA_E_NOTSUPPORTED || ex.HResult == E_NOTIMPL || ex.HResult == E_NOINTERFACE;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Marshal.IsComObject(_element))
        {
            Marshal.ReleaseComObject(_element);
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
            return _pattern.SupportedTextSelection != SupportedTextSelection.SupportedTextSelection_None;
        }
    }

    public IReadOnlyList<IUiaTextRange> GetSelection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IUIAutomationTextRangeArray? arr = null;
        try
        {
            arr = _pattern.GetSelection();
            if (arr == null)
            {
                return [];
            }

            int len = arr.Length;
            if (len > UiaProtocolConstants.MaxRanges)
            {
                if (Marshal.IsComObject(arr)) Marshal.ReleaseComObject(arr);
                arr = null;
                throw new InvalidOperationException("RANGE_COUNT_EXCEEDED");
            }

            var list = new List<IUiaTextRange>(len);
            try
            {
                for (int i = 0; i < len; i++)
                {
                    var r = arr.GetElement(i);
                    if (r != null)
                    {
                        list.Add(new RealUiaTextRange(r));
                    }
                }
                return list;
            }
            catch
            {
                foreach (var r in list)
                {
                    try { r.Dispose(); } catch { }
                }
                list.Clear();
                throw;
            }
        }
        finally
        {
            if (arr != null && Marshal.IsComObject(arr))
            {
                try { Marshal.ReleaseComObject(arr); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Marshal.IsComObject(_pattern))
        {
            Marshal.ReleaseComObject(_pattern);
        }
    }
}

internal sealed unsafe class RealUiaTextRange : IUiaTextRange
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
        var bstr = _range.GetText(maxLength);
        try
        {
            return bstr.ToString();
        }
        finally
        {
            if (bstr.Value != null)
            {
                Marshal.FreeBSTR((nint)bstr.Value);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Marshal.IsComObject(_range))
        {
            Marshal.ReleaseComObject(_range);
        }
    }
}
