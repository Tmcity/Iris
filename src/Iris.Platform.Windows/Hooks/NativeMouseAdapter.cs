using System.Runtime.InteropServices;
using Iris.Core.Imaging;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Hooks;

internal sealed class NativeMouseAdapter(IMouseHookApi api)
{
    internal static MouseHookRegistration CreateRegistration(Func<int, nuint, nint, nint> callback) => new()
    {
        Callback = (code, message, data) => new LRESULT(callback(code, message.Value, data.Value)),
    };

    internal void CreateQueue() => api.PeekMessage();
    internal uint CurrentThreadId() => api.CurrentThreadId();
    internal uint CurrentProcessId() => api.CurrentProcessId();
    internal nint LoadIBeam() => api.LoadCursor(0, 32513); // Shared IDC_IBEAM; never destroyed.
    internal void Install(MouseHookRegistration registration) =>
        registration.Handle = api.SetHook(14, registration.Callback, api.ModuleHandle(), 0); // WH_MOUSE_LL
    internal bool Unhook(MouseHookRegistration registration) => api.Unhook(registration.Handle);
    internal bool PostQuit(uint thread) => api.PostThreadMessage(thread, 0x12);
    internal int PumpMessage()
    {
        int result = api.GetMessage(out var message);
        if (result > 0) { api.TranslateMessage(in message); api.DispatchMessage(in message); }
        return result;
    }
    internal nint CallNext(int code, nuint message, nint data) => api.CallNext(code, message, data);
    internal uint TickCount() => api.TickCount();
    internal int LastError() => api.LastError();
    internal ClickMetrics Metrics(double scale)
    {
        double value = scale * 96;
        if (!double.IsFinite(value) || value < 1 || value > uint.MaxValue) return default;
        uint dpi = checked((uint)Math.Round(value));
        return new(api.DoubleClickTime(), api.SystemMetricsForDpi(36, dpi), api.SystemMetricsForDpi(37, dpi));
    }
    internal MouseSample? Read(nuint message, nint data, nint iBeam)
    {
        var kind = (MouseMessage)message;
        if (kind is not (MouseMessage.Move or MouseMessage.LeftDown or MouseMessage.LeftUp or
            MouseMessage.RightDown or MouseMessage.RightUp or MouseMessage.MiddleDown or MouseMessage.MiddleUp or
            MouseMessage.XDown or MouseMessage.XUp or MouseMessage.Wheel or MouseMessage.HorizontalWheel)) return null;
        var raw = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
        var sample = new MouseSample(kind, new PixelPoint(raw.pt.X, raw.pt.Y), raw.time, raw.flags);
        if (sample.IsDown || sample.IsUp)
        {
            nint window = api.ForegroundWindow();
            uint pid = 0;
            if (window != 0 && api.WindowThreadProcessId(window, out pid) == 0) pid = 0;
            sample = sample with { Source = new(window, pid) };
        }
        if (sample.IsDown)
        {
            var cursor = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
            bool visibleIBeam = api.GetCursorInfo(ref cursor) && (uint)cursor.flags == 1 &&
                iBeam != 0 && cursor.hCursor == (HCURSOR)iBeam;
            sample = sample with { IBeam = visibleIBeam, Shift = (api.GetAsyncKeyState(0x10) & 0x8000) != 0 };
        }
        return sample;
    }
}
