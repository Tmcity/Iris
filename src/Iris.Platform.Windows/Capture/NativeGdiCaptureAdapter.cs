using System.Runtime.InteropServices;
using Iris.Core.Imaging;
using Windows.Win32.Graphics.Gdi;

namespace Iris.Platform.Windows.Capture;

internal sealed unsafe class NativeGdiCaptureAdapter(IGdiCaptureApi api)
{
    internal nint EnterDpi() => api.SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
    internal bool RestoreDpi(nint original) => api.SetThreadDpiAwarenessContext(original) != 0;
    internal nint ScreenDC() => api.GetDC(0);
    internal nint MemoryDC(nint screen) => api.CreateCompatibleDC(screen);
    internal (nint Bitmap, nint Bits) Bitmap(nint dc, int width, int height)
    {
        int size = ScreenPixelLayout.ValidateSize(width, height, Iris.Core.Errors.ErrorCode.CaptureFailed);
        var info = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1,
            biBitCount = 32, biCompression = 0, biSizeImage = (uint)size,
        } };
        nint bitmap = api.CreateDIBSection(dc, in info, 0, out nint bits, 0, 0);
        return (bitmap, bits);
    }
    internal nint Select(nint dc, nint value) => api.SelectObject(dc, value);
    internal static bool ValidSelected(nint value) => value != 0 && value != -1;
    internal static void Clear(nint bits, int length) => new Span<byte>((void*)bits, length).Clear();
    internal static void Copy(nint bits, byte[] target) => new ReadOnlySpan<byte>((void*)bits, target.Length).CopyTo(target);
    internal bool Blit(nint memory, nint screen, PixelRect monitor, PixelRect union) => api.BitBlt(memory,
        checked((int)((long)monitor.X - union.X)), checked((int)((long)monitor.Y - union.Y)), monitor.Width, monitor.Height,
        screen, monitor.X, monitor.Y, 0x40CC0020); // SRCCOPY | CAPTUREBLT
    internal bool Flush() => api.GdiFlush();
    internal bool DeleteBitmap(nint bitmap) => api.DeleteObject(bitmap);
    internal bool DeleteMemoryDC(nint dc) => api.DeleteDC(dc);
    internal bool ReleaseScreenDC(nint dc) => api.ReleaseDC(0, dc) != 0;
    internal int LastError() => api.LastError(); // Called only for CreateDIBSection / BitBlt documented failures.
}
