using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Clipboard;

internal sealed unsafe class NativeClipboardSnapshotAdapter(Action? prepareThread = null) : IClipboardSnapshotNative
{
    internal static readonly NativeClipboardSnapshotAdapter Instance = new();
    private static NativeClipboardAdapter Basic => NativeClipboardAdapter.Instance;
    public nint CreateOwnerWindow()
    {
        prepareThread?.Invoke();
        return Basic.CreateOwnerWindow();
    }
    public bool DestroyWindow(nint hwnd) => Basic.DestroyWindow(hwnd);
    public bool OpenClipboard(nint hwnd) => Basic.OpenClipboard(hwnd);
    public bool CloseClipboard() => Basic.CloseClipboard();
    public bool EmptyClipboard() => Basic.EmptyClipboard();
    public nint SetClipboardData(uint format, nint hMem) => Basic.SetClipboardData(format, hMem);
    public nint GetClipboardData(uint format) => Basic.GetClipboardData(format);
    public bool IsClipboardFormatAvailable(uint format) => Basic.IsClipboardFormatAvailable(format);
    public uint GetClipboardSequenceNumber() => Basic.GetClipboardSequenceNumber();
    public nint GlobalAlloc(uint uFlags, nuint dwBytes) => Basic.GlobalAlloc(uFlags, dwBytes);
    public nint GlobalFree(nint hMem) => Basic.GlobalFree(hMem);
    public nint GlobalLock(nint hMem) => Basic.GlobalLock(hMem);
    public bool GlobalUnlock(nint hMem) => Basic.GlobalUnlock(hMem);
    public nuint GlobalSize(nint hMem) => Basic.GlobalSize(hMem);
    public int GetLastError() => Basic.GetLastError();
    public uint EnumFormats(uint previous)
    {
        PInvoke.SetLastError(0);
        return PInvoke.EnumClipboardFormats(previous);
    }
    public string GetFormatName(uint format)
    {
        Span<char> name = stackalloc char[256];
        int length = PInvoke.GetClipboardFormatName(format, name);
        if (length == 0) throw new InvalidOperationException("FORMAT_NAME_FAILED");
        return new string(name[..length]);
    }
    public nint GetOwner() => (nint)PInvoke.GetClipboardOwner().Value;
    public uint GetOwnerProcessId(nint owner)
    {
        uint pid = 0;
        if (PInvoke.GetWindowThreadProcessId(new HWND((void*)owner), &pid) == 0 || pid == 0)
            throw new InvalidOperationException("OWNER_ID_FAILED");
        return pid;
    }
    public void CopyFrom(nint pointer, Span<byte> destination) => new ReadOnlySpan<byte>((void*)pointer, destination.Length).CopyTo(destination);
    public void CopyTo(ReadOnlySpan<byte> source, nint pointer) => source.CopyTo(new Span<byte>((void*)pointer, source.Length));
    public void PumpMessages()
    {
        for (int i = 0; i < 64 && PInvoke.PeekMessage(out var message, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE); i++)
        {
            if (message.message == 0x12) throw new InvalidOperationException("MESSAGE_LOOP_QUIT");
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }
    }
    public void WaitForWork(WaitHandle work)
    {
        bool added = false;
        try
        {
            work.SafeWaitHandle.DangerousAddRef(ref added);
            HANDLE handle = new((void*)work.SafeWaitHandle.DangerousGetHandle());
            var result = PInvoke.MsgWaitForMultipleObjectsEx(1, &handle, uint.MaxValue,
                QUEUE_STATUS_FLAGS.QS_ALLINPUT, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
            if ((uint)result == uint.MaxValue) throw new InvalidOperationException("MESSAGE_WAIT_FAILED");
        }
        finally { if (added) work.SafeWaitHandle.DangerousRelease(); }
    }
}
