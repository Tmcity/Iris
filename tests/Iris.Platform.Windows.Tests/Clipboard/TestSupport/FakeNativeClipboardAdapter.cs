using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iris.Platform.Windows.Clipboard;

namespace Iris.Platform.Windows.Tests.Clipboard.TestSupport;

internal sealed class FakeNativeClipboardAdapter : INativeClipboardAdapter
{
    private long _nextHandle = 1000;
    private long _nextHwnd = 5000;

    public ConcurrentQueue<string> CallLog { get; } = new();

    public List<nint> CreatedWindows { get; } = [];
    public List<nint> DestroyedWindows { get; } = [];
    public List<nint> AllocatedHGlobals { get; } = [];
    public List<nint> FreedHGlobals { get; } = [];

    public Dictionary<uint, nint> ClipboardFormats { get; } = [];
    public Dictionary<nint, byte[]> MemoryBlocks { get; } = [];

    public Func<nint, bool>? OnOpenClipboard { get; set; }
    public Func<bool>? OnCloseClipboard { get; set; }
    public Func<bool>? OnEmptyClipboard { get; set; }
    public Func<uint, nint, nint>? OnSetClipboardData { get; set; }
    public Func<uint, nint>? OnGetClipboardData { get; set; }
    public Func<uint, bool>? OnIsClipboardFormatAvailable { get; set; }
    public Func<nint>? OnCreateOwnerWindow { get; set; }
    public Func<uint, nuint, nint>? OnGlobalAlloc { get; set; }
    public Func<nint, nint>? OnGlobalLock { get; set; }

    public Action? OnBeforeEmptyClipboard { get; set; }
    public Action? OnBeforeSetClipboardData { get; set; }

    public int LastError { get; set; }
    public uint SequenceNumber { get; set; } = 42;
    public bool IsClipboardOpen { get; private set; }
    public nint CurrentOwnerHwnd { get; private set; }

    public nint CreateOwnerWindow()
    {
        CallLog.Enqueue("CreateOwnerWindow");
        if (OnCreateOwnerWindow != null)
        {
            var custom = OnCreateOwnerWindow();
            if (custom != 0) CreatedWindows.Add(custom);
            return custom;
        }

        var hwnd = (nint)Interlocked.Increment(ref _nextHwnd);
        CreatedWindows.Add(hwnd);
        return hwnd;
    }

    public void DestroyWindow(nint hwnd)
    {
        CallLog.Enqueue($"DestroyWindow({hwnd})");
        DestroyedWindows.Add(hwnd);
    }

    public bool OpenClipboard(nint hwnd)
    {
        CallLog.Enqueue($"OpenClipboard({hwnd})");
        if (OnOpenClipboard != null)
        {
            bool res = OnOpenClipboard(hwnd);
            if (res)
            {
                IsClipboardOpen = true;
                CurrentOwnerHwnd = hwnd;
            }
            return res;
        }

        IsClipboardOpen = true;
        CurrentOwnerHwnd = hwnd;
        return true;
    }

    public bool CloseClipboard()
    {
        CallLog.Enqueue("CloseClipboard");
        if (OnCloseClipboard != null)
        {
            return OnCloseClipboard();
        }

        IsClipboardOpen = false;
        CurrentOwnerHwnd = 0;
        return true;
    }

    public bool EmptyClipboard()
    {
        CallLog.Enqueue("EmptyClipboard");
        OnBeforeEmptyClipboard?.Invoke();

        if (OnEmptyClipboard != null)
        {
            return OnEmptyClipboard();
        }

        ClipboardFormats.Clear();
        return true;
    }

    public nint SetClipboardData(uint format, nint hMem)
    {
        CallLog.Enqueue($"SetClipboardData({format}, {hMem})");
        OnBeforeSetClipboardData?.Invoke();

        if (OnSetClipboardData != null)
        {
            return OnSetClipboardData(format, hMem);
        }

        ClipboardFormats[format] = hMem;
        return hMem;
    }

    public nint GetClipboardData(uint format)
    {
        CallLog.Enqueue($"GetClipboardData({format})");
        if (OnGetClipboardData != null)
        {
            return OnGetClipboardData(format);
        }

        return ClipboardFormats.GetValueOrDefault(format, 0);
    }

    public bool IsClipboardFormatAvailable(uint format)
    {
        CallLog.Enqueue($"IsClipboardFormatAvailable({format})");
        if (OnIsClipboardFormatAvailable != null)
        {
            return OnIsClipboardFormatAvailable(format);
        }

        return ClipboardFormats.ContainsKey(format);
    }

    public uint GetClipboardSequenceNumber()
    {
        CallLog.Enqueue("GetClipboardSequenceNumber");
        return SequenceNumber;
    }

    public nint GlobalAlloc(uint uFlags, nuint dwBytes)
    {
        CallLog.Enqueue($"GlobalAlloc({uFlags}, {dwBytes})");
        if (OnGlobalAlloc != null)
        {
            var custom = OnGlobalAlloc(uFlags, dwBytes);
            if (custom != 0) AllocatedHGlobals.Add(custom);
            return custom;
        }

        var handle = (nint)Interlocked.Increment(ref _nextHandle);
        AllocatedHGlobals.Add(handle);
        MemoryBlocks[handle] = new byte[(int)dwBytes];
        return handle;
    }

    public nint GlobalFree(nint hMem)
    {
        CallLog.Enqueue($"GlobalFree({hMem})");
        FreedHGlobals.Add(hMem);
        MemoryBlocks.Remove(hMem);
        return 0;
    }

    private readonly Dictionary<nint, GCHandle> _pinnedHandles = [];

    public unsafe nint GlobalLock(nint hMem)
    {
        CallLog.Enqueue($"GlobalLock({hMem})");
        if (OnGlobalLock != null)
        {
            return OnGlobalLock(hMem);
        }

        if (MemoryBlocks.TryGetValue(hMem, out var buffer))
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            _pinnedHandles[hMem] = handle;
            return handle.AddrOfPinnedObject();
        }

        return 0;
    }

    public bool GlobalUnlock(nint hMem)
    {
        CallLog.Enqueue($"GlobalUnlock({hMem})");
        if (_pinnedHandles.Remove(hMem, out var handle))
        {
            handle.Free();
        }
        return true;
    }

    public nuint GlobalSize(nint hMem)
    {
        CallLog.Enqueue($"GlobalSize({hMem})");
        if (MemoryBlocks.TryGetValue(hMem, out var buffer))
        {
            return (nuint)buffer.Length;
        }
        return 0;
    }

    public int GetLastError()
    {
        return LastError;
    }
}
