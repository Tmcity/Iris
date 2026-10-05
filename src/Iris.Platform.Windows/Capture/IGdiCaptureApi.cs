using Windows.Win32.Graphics.Gdi;

namespace Iris.Platform.Windows.Capture;

internal interface IGdiCaptureApi
{
    nint SetThreadDpiAwarenessContext(nint context);
    nint GetDC(nint window);
    int ReleaseDC(nint window, nint dc);
    nint CreateCompatibleDC(nint screen);
    nint CreateDIBSection(nint dc, in BITMAPINFO info, uint usage, out nint bits, nint section, uint offset);
    nint SelectObject(nint dc, nint value);
    bool DeleteObject(nint bitmap);
    bool DeleteDC(nint dc);
    bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rasterOperation);
    bool GdiFlush();
    int LastError();
}
