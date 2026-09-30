using System;

namespace Iris.Platform.Windows.Clipboard;

internal interface INativeClipboardAdapter
{
    nint CreateOwnerWindow();

    bool DestroyWindow(nint hwnd);

    bool OpenClipboard(nint hwnd);

    bool CloseClipboard();

    bool EmptyClipboard();

    nint SetClipboardData(uint format, nint hMem);

    nint GetClipboardData(uint format);

    bool IsClipboardFormatAvailable(uint format);

    uint GetClipboardSequenceNumber();

    nint GlobalAlloc(uint uFlags, nuint dwBytes);

    nint GlobalFree(nint hMem);

    nint GlobalLock(nint hMem);

    bool GlobalUnlock(nint hMem);

    nuint GlobalSize(nint hMem);

    int GetLastError();
}
