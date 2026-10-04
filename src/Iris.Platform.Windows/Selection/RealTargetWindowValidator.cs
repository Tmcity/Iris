using System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Iris.Platform.Windows.Selection;

internal sealed unsafe class RealTargetWindowValidator : ITargetWindowValidator
{
    public static readonly RealTargetWindowValidator Instance = new();

    public bool IsTargetValidAndForeground(nint expectedWindow, uint expectedProcessId)
    {
        if (expectedWindow == 0 || expectedProcessId == 0)
        {
            return false;
        }

        var hwnd = new HWND((void*)expectedWindow);
        if (!PInvoke.IsWindow(hwnd))
        {
            return false;
        }

        var fgHwnd = PInvoke.GetForegroundWindow();
        if ((nint)fgHwnd.Value != expectedWindow)
        {
            return false;
        }

        uint fgPid = 0;
        uint threadId = PInvoke.GetWindowThreadProcessId(fgHwnd, &fgPid);
        if (threadId == 0 || fgPid != expectedProcessId)
        {
            return false;
        }

        var currentPid = PInvoke.GetCurrentProcessId();
        if (fgPid == currentPid)
        {
            return false; // Target is Iris itself
        }

        return true;
    }
}
