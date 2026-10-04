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

    private readonly Func<HRESULT>? _coInitFunc;
    private readonly Action? _coUninitAction;
    private readonly Func<object?>? _createUiaFunc;
    private readonly Action<object>? _releaseComAction;

    public RealNativeUiaAdapter() { }

    internal RealNativeUiaAdapter(
        Func<HRESULT>? coInitFunc = null,
        Action? coUninitAction = null,
        Func<object?>? createUiaFunc = null,
        Action<object>? releaseComAction = null)
    {
        _coInitFunc = coInitFunc;
        _coUninitAction = coUninitAction;
        _createUiaFunc = createUiaFunc;
        _releaseComAction = releaseComAction;
    }

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

        var hrCoInit = _coInitFunc != null
            ? _coInitFunc()
            : PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);

        if (hrCoInit.Failed)
        {
            hrCoInit.ThrowOnFailure();
        }
        bool coInitSucceeded = !hrCoInit.Failed;
        int ownerThreadId = Environment.CurrentManagedThreadId;

        object? comObj = null;

        if (_createUiaFunc != null)
        {
            try
            {
                comObj = _createUiaFunc();
                if (comObj == null)
                {
                    throw new InvalidOperationException("Failed to create UIA instance from custom factory.");
                }
            }
            catch (Exception ex)
            {
                List<Exception>? rollbackExceptions = null;
                if (coInitSucceeded)
                {
                    try
                    {
                        if (_coUninitAction != null) _coUninitAction();
                        else PInvoke.CoUninitialize();
                    }
                    catch (Exception uninitEx)
                    {
                        (rollbackExceptions ??= []).Add(uninitEx);
                    }
                }
                if (rollbackExceptions != null && rollbackExceptions.Count > 0)
                {
                    throw new AggregateException("Failed to initialize COM UIA session and rollback failed.", [ex, .. rollbackExceptions]);
                }
                throw;
            }
        }
        else
        {
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
                catch (Exception ex)
                {
                    List<Exception>? rollbackExceptions = null;
                    if (coInitSucceeded)
                    {
                        try
                        {
                            if (_coUninitAction != null) _coUninitAction();
                            else PInvoke.CoUninitialize();
                        }
                        catch (Exception uninitEx)
                        {
                            (rollbackExceptions ??= []).Add(uninitEx);
                        }
                    }
                    if (rollbackExceptions != null && rollbackExceptions.Count > 0)
                    {
                        throw new AggregateException("Failed to initialize COM UIA session and rollback failed.", [ex, .. rollbackExceptions]);
                    }
                    throw;
                }
            }
        }

        try
        {
            if (comObj is IUIAutomation2 uia2)
            {
                uia2.ConnectionTimeout = (uint)connectionTimeoutMs;
                uia2.TransactionTimeout = (uint)transactionTimeoutMs;
            }

            if (comObj is not IUIAutomation uia)
            {
                throw new InvalidCastException($"COM object of type '{comObj?.GetType().FullName ?? "null"}' does not implement IUIAutomation.");
            }

            return new RealUiaAutomationSession(uia, ownerThreadId, coInitSucceeded, _coUninitAction, _releaseComAction);
        }
        catch (Exception ex)
        {
            List<Exception>? rollbackExceptions = null;
            try
            {
                if (_releaseComAction != null) _releaseComAction(comObj);
                else Marshal.ReleaseComObject(comObj);
            }
            catch (Exception relEx)
            {
                (rollbackExceptions ??= []).Add(relEx);
            }

            if (coInitSucceeded)
            {
                try
                {
                    if (_coUninitAction != null) _coUninitAction();
                    else PInvoke.CoUninitialize();
                }
                catch (Exception uninitEx)
                {
                    (rollbackExceptions ??= []).Add(uninitEx);
                }
            }

            if (rollbackExceptions != null && rollbackExceptions.Count > 0)
            {
                throw new AggregateException("Failed to initialize COM UIA session and rollback failed.", [ex, .. rollbackExceptions]);
            }
            throw;
        }
    }
}

internal sealed class RealUiaAutomationSession : IUiaAutomationSession
{
    private readonly IUIAutomation _uia;
    private readonly int _ownerThreadId;
    private readonly bool _coInitSucceeded;
    private readonly Action? _coUninitAction;
    private readonly Action<object>? _releaseComAction;
    private bool _uiaReleased;
    private bool _coUninitialized;
    private bool _disposed;

    public RealUiaAutomationSession(
        IUIAutomation uia,
        int ownerThreadId = 0,
        bool coInitSucceeded = false,
        Action? coUninitAction = null,
        Action<object>? releaseComAction = null)
    {
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
        _ownerThreadId = ownerThreadId != 0 ? ownerThreadId : Environment.CurrentManagedThreadId;
        _coInitSucceeded = coInitSucceeded;
        _coUninitAction = coUninitAction;
        _releaseComAction = releaseComAction;
    }

    public IUiaElement? GetFocusedElement()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var element = _uia.GetFocusedElement();
        return element != null ? new RealUiaElement(element, _uia, _releaseComAction) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("RealUiaAutomationSession must be disposed on the same thread it was created on.");
        }

        List<Exception>? exceptions = null;

        if (!_uiaReleased)
        {
            try
            {
                if (_releaseComAction != null)
                {
                    _releaseComAction(_uia);
                }
                else if (Marshal.IsComObject(_uia))
                {
                    Marshal.ReleaseComObject(_uia);
                }
                _uiaReleased = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        if (_coInitSucceeded && !_coUninitialized)
        {
            try
            {
                if (_coUninitAction != null)
                {
                    _coUninitAction();
                }
                else
                {
                    PInvoke.CoUninitialize();
                }
                _coUninitialized = true;
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        if (exceptions != null && exceptions.Count > 0)
        {
            throw new AggregateException("Failed to dispose one or more COM resources.", exceptions);
        }

        _disposed = true;
    }
}

internal sealed class RealUiaElement : IUiaElement
{
    private readonly IUIAutomationElement _element;
    private readonly IUIAutomation _uia;
    private readonly Action<object>? _releaseComAction;
    private bool _disposed;

    public RealUiaElement(IUIAutomationElement element, IUIAutomation uia, Action<object>? releaseComAction = null)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
        _releaseComAction = releaseComAction;
    }

    public unsafe uint ProcessId => (uint)_element.CurrentProcessId;
    public unsafe nint NativeWindowHandle => (nint)_element.CurrentNativeWindowHandle.Value;
    public bool IsPassword => _element.CurrentIsPassword != 0;

    public IUiaElement? GetParent(int maxDepth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IUIAutomationTreeWalker? walker = null;
        RealUiaElement? parentElement = null;
        bool transferred = false;
        try
        {
            try
            {
                walker = _uia.ControlViewWalker;
                var parent = walker?.GetParentElement(_element);
                if (parent != null)
                {
                    parentElement = new RealUiaElement(parent, _uia, _releaseComAction);
                }
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80040201)) // UIA_E_ELEMENTNOTAVAILABLE
            {
                parentElement = null;
            }

            if (walker != null)
            {
                var w = walker;
                walker = null;
                ReleaseComObjectInternal(w);
            }

            transferred = true;
            return parentElement;
        }
        catch (Exception ex)
        {
            List<Exception>? rollbackExceptions = null;
            if (parentElement != null)
            {
                try
                {
                    parentElement.Dispose();
                    parentElement = null;
                }
                catch (Exception dex)
                {
                    (rollbackExceptions ??= []).Add(dex);
                }
            }

            if (walker != null)
            {
                try
                {
                    var w = walker;
                    walker = null;
                    ReleaseComObjectInternal(w);
                }
                catch (Exception wex)
                {
                    (rollbackExceptions ??= []).Add(wex);
                }
            }

            if (rollbackExceptions != null && rollbackExceptions.Count > 0)
            {
                throw new AggregateException("Failed to get parent element and rollback encountered errors.", [ex, .. rollbackExceptions]);
            }
            throw;
        }
        finally
        {
            if (!transferred)
            {
                if (parentElement != null)
                {
                    try { parentElement.Dispose(); } catch { }
                }
                if (walker != null)
                {
                    try { ReleaseComObjectInternal(walker); } catch { }
                }
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
            return new RealUiaTextPattern(pattern, _releaseComAction);
        }

        if (patternObj != null)
        {
            ReleaseComObjectInternal(patternObj);
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

    private void ReleaseComObjectInternal(object? obj)
    {
        if (obj == null) return;
        if (_releaseComAction != null) _releaseComAction(obj);
        else if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseComObjectInternal(_element);
        _disposed = true;
    }
}

internal sealed class RealUiaTextPattern : IUiaTextPattern
{
    private readonly IUIAutomationTextPattern _pattern;
    private readonly Action<object>? _releaseComAction;
    private bool _disposed;

    public RealUiaTextPattern(IUIAutomationTextPattern pattern, Action<object>? releaseComAction = null)
    {
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _releaseComAction = releaseComAction;
    }

    public bool SupportsSelection
    {
        get
        {
            return _pattern.SupportedTextSelection != SupportedTextSelection.SupportedTextSelection_None;
        }
    }

    private void ReleaseComObjectInternal(object? obj)
    {
        if (obj == null) return;
        if (_releaseComAction != null) _releaseComAction(obj);
        else if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
    }

    public IReadOnlyList<IUiaTextRange> GetSelection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IUIAutomationTextRangeArray? arr = null;
        var list = new List<IUiaTextRange>();
        bool transferred = false;
        try
        {
            arr = _pattern.GetSelection();
            if (arr == null)
            {
                transferred = true;
                return [];
            }

            int len = arr.Length;
            if (len > UiaProtocolConstants.MaxRanges)
            {
                throw new InvalidOperationException("RANGE_COUNT_EXCEEDED");
            }

            for (int i = 0; i < len; i++)
            {
                var r = arr.GetElement(i);
                if (r != null)
                {
                    list.Add(new RealUiaTextRange(r, _releaseComAction));
                }
            }

            if (arr != null)
            {
                var toRelease = arr;
                arr = null;
                ReleaseComObjectInternal(toRelease);
            }

            transferred = true;
            return list;
        }
        catch (Exception ex)
        {
            List<Exception>? rollbackExceptions = null;
            foreach (var r in list)
            {
                try
                {
                    r.Dispose();
                }
                catch (Exception dex)
                {
                    (rollbackExceptions ??= []).Add(dex);
                }
            }
            list.Clear();

            if (arr != null)
            {
                try
                {
                    var toRelease = arr;
                    arr = null;
                    ReleaseComObjectInternal(toRelease);
                }
                catch (Exception aex)
                {
                    (rollbackExceptions ??= []).Add(aex);
                }
            }

            if (rollbackExceptions != null && rollbackExceptions.Count > 0)
            {
                throw new AggregateException("Failed to get selection and rollback encountered errors.", [ex, .. rollbackExceptions]);
            }
            throw;
        }
        finally
        {
            if (!transferred)
            {
                foreach (var r in list)
                {
                    try { r.Dispose(); } catch { }
                }
                list.Clear();

                if (arr != null)
                {
                    try { ReleaseComObjectInternal(arr); arr = null; } catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseComObjectInternal(_pattern);
        _disposed = true;
    }
}

internal sealed unsafe class RealUiaTextRange : IUiaTextRange
{
    private readonly IUIAutomationTextRange _range;
    private readonly Action<object>? _releaseComAction;
    private bool _disposed;

    public RealUiaTextRange(IUIAutomationTextRange range, Action<object>? releaseComAction = null)
    {
        _range = range ?? throw new ArgumentNullException(nameof(range));
        _releaseComAction = releaseComAction;
    }

    private void ReleaseComObjectInternal(object? obj)
    {
        if (obj == null) return;
        if (_releaseComAction != null) _releaseComAction(obj);
        else if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
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
        ReleaseComObjectInternal(_range);
        _disposed = true;
    }
}
