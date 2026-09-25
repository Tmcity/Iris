using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.SystemIntegration;

public static class FatalErrorDialog
{
    /// <summary>置顶的错误对话框（Win32 MessageBoxW：MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND），阻塞直到用户关闭。</summary>
    public static void Show(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var flags = MESSAGEBOX_STYLE.MB_OK
            | MESSAGEBOX_STYLE.MB_ICONERROR
            | MESSAGEBOX_STYLE.MB_TOPMOST
            | MESSAGEBOX_STYLE.MB_SETFOREGROUND;

        PInvoke.MessageBox(HWND.Null, message, title, flags);
    }
}
