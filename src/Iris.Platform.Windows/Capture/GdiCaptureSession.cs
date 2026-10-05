using Iris.Core.Imaging;

namespace Iris.Platform.Windows.Capture;

// Every member is accessed on the owning worker, including retries after failures.
internal sealed class GdiCaptureSession(NativeGdiCaptureAdapter native)
{
    private nint _dpi, _screen, _memory, _bitmap, _bits, _oldObject;
    private bool _selected;
    internal bool HasResources => _dpi != 0 || _screen != 0 || _memory != 0 || _bitmap != 0;
    internal void EnterDpi()
    {
        _dpi = native.EnterDpi();
        if (_dpi == 0) throw ScreenCaptureErrors.Fail("DPI_ENTER");
    }
    internal byte[] Capture(PixelRect union, IReadOnlyList<Iris.Core.Platform.MonitorInfo> monitors, Action check)
    {
        int bytes = ScreenPixelLayout.ValidateSize(union.Width, union.Height, Iris.Core.Errors.ErrorCode.CaptureFailed);
        check();
        var pixels = new byte[bytes];
        _screen = native.ScreenDC();
        if (_screen == 0) throw ScreenCaptureErrors.Fail("GET_DC");
        check();
        _memory = native.MemoryDC(_screen);
        if (_memory == 0) throw ScreenCaptureErrors.Fail("CREATE_DC");
        check();
        (_bitmap, _bits) = native.Bitmap(_screen, union.Width, union.Height);
        if (_bitmap == 0) throw ScreenCaptureErrors.Fail("CREATE_DIB", nativeError: native.LastError());
        if (_bits == 0) throw ScreenCaptureErrors.Fail("DIB_BITS"); // Bitmap ownership exists even when bits are invalid.
        check();
        _oldObject = native.Select(_memory, _bitmap);
        if (!NativeGdiCaptureAdapter.ValidSelected(_oldObject)) { _oldObject = 0; throw ScreenCaptureErrors.Fail("SELECT_BITMAP"); }
        _selected = true;
        check();
        if (!native.Flush()) throw ScreenCaptureErrors.Fail("FLUSH_BEFORE_CLEAR");
        check(); NativeGdiCaptureAdapter.Clear(_bits, bytes); check();
        foreach (var monitor in monitors)
        {
            check();
            if (!native.Blit(_memory, _screen, monitor.Bounds, union)) throw ScreenCaptureErrors.Fail("BITBLT", nativeError: native.LastError());
            check();
        }
        if (!native.Flush()) throw ScreenCaptureErrors.Fail("FLUSH_BEFORE_COPY");
        check(); NativeGdiCaptureAdapter.Copy(_bits, pixels); check();
        int stride = checked(union.Width * 4);
        for (int row = 0; row < union.Height; row++)
        {
            check();
            for (int x = 3; x < stride; x += 4) pixels[row * stride + x] = 255;
        }
        check(); return pixels;
    }
    internal IReadOnlyList<string> Cleanup()
    {
        var errors = new List<string>();
        bool bitmapAttempted = false;
        if (_selected)
        {
            Attempt("RESTORE_SELECTION", () =>
            {
                if (!NativeGdiCaptureAdapter.ValidSelected(native.Select(_memory, _oldObject))) return false;
                _selected = false; _oldObject = 0; return true;
            }, errors);
        }
        if (_bitmap != 0 && !_selected)
        {
            bitmapAttempted = true;
            Attempt("DELETE_BITMAP", DeleteBitmap, errors);
        }
        if (_memory != 0)
        {
            Attempt("DELETE_DC", () =>
            {
                if (!native.DeleteMemoryDC(_memory)) return false;
                _memory = 0; _selected = false; _oldObject = 0; return true;
            }, errors);
        }
        // If restoring the selection failed, deleting its DC first safely detaches the bitmap.
        if (_bitmap != 0 && !_selected && !bitmapAttempted) Attempt("DELETE_BITMAP", DeleteBitmap, errors);
        if (_screen != 0)
        {
            Attempt("RELEASE_DC", () =>
            {
                if (!native.ReleaseScreenDC(_screen)) return false;
                _screen = 0; return true;
            }, errors);
        }
        if (_dpi != 0)
        {
            Attempt("DPI_RESTORE", () =>
            {
                if (!native.RestoreDpi(_dpi)) return false;
                _dpi = 0; return true;
            }, errors);
        }
        return errors;
    }
    private bool DeleteBitmap()
    {
        if (!native.DeleteBitmap(_bitmap)) return false;
        _bitmap = _bits = 0; return true;
    }
    private static void Attempt(string stage, Func<bool> cleanup, List<string> errors)
    {
        try { if (!cleanup()) errors.Add($"stage={stage};result=0"); }
        catch (Exception ex) { errors.Add(ScreenCaptureErrors.Describe(stage, ex)); }
    }
}
