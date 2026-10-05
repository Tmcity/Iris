using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Iris.Platform.Windows.Capture;

internal sealed unsafe class Win32GdiCaptureApi : IGdiCaptureApi
{
    public nint SetThreadDpiAwarenessContext(nint context) => (nint)PInvoke.SetThreadDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((void*)context)).Value;
    public nint GetDC(nint window) => (nint)PInvoke.GetDC(new HWND((void*)window)).Value;
    public int ReleaseDC(nint window, nint dc) => PInvoke.ReleaseDC(new HWND((void*)window), new HDC((void*)dc));
    public nint CreateCompatibleDC(nint screen) => (nint)PInvoke.CreateCompatibleDC(new HDC((void*)screen)).Value;
    public nint CreateDIBSection(nint dc, in BITMAPINFO info, uint usage, out nint bits, nint section, uint offset)
    {
        void* pointer = null;
        fixed (BITMAPINFO* header = &info)
        {
            var bitmap = PInvoke.CreateDIBSection(new HDC((void*)dc), header, (DIB_USAGE)usage, &pointer, new HANDLE((void*)section), offset);
            bits = (nint)pointer;
            return (nint)bitmap.Value;
        }
    }
    public nint SelectObject(nint dc, nint value) => (nint)PInvoke.SelectObject(new HDC((void*)dc), new HGDIOBJ((void*)value)).Value;
    public bool DeleteObject(nint bitmap) => PInvoke.DeleteObject(new HGDIOBJ((void*)bitmap));
    public bool DeleteDC(nint dc) => PInvoke.DeleteDC(new HDC((void*)dc));
    public bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rasterOperation) =>
        PInvoke.BitBlt(new HDC((void*)target), x, y, width, height, new HDC((void*)source), sourceX, sourceY, (ROP_CODE)rasterOperation);
    public bool GdiFlush() => PInvoke.GdiFlush();
    public int LastError() => Marshal.GetLastPInvokeError();
}
