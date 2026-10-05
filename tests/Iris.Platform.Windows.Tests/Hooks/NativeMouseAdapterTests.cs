using System.Runtime.InteropServices;
using Iris.Core.Imaging;
using Iris.Platform.Windows.Hooks;
using Shouldly;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Iris.Platform.Windows.Tests.Hooks;

public sealed class NativeMouseAdapterTests
{
    [Fact]
    public void NativeAdapter_UsesExactHookMessageCursorAndDpiShapes()
    {
        using var api = new FakeMouseHookApi();
        var adapter = new NativeMouseAdapter(api);
        var hook = NativeMouseAdapter.CreateRegistration((code, message, data) => adapter.CallNext(code, message, data));
        adapter.CreateQueue();
        adapter.CurrentThreadId().ShouldBe((uint)Environment.CurrentManagedThreadId);
        adapter.LoadIBeam().ShouldBe((nint)900);
        api.CursorRequest.ShouldBe(((nint)0, 32513));
        try
        {
            adapter.Install(hook);
            api.InstalledId.ShouldBe(14);
            api.InstalledModule.ShouldBe((nint)77);
            api.InstalledThread.ShouldBe(0u);
            hook.Callback(-1, new WPARAM(456), new LPARAM(789)).Value.ShouldBe((nint)123);
            api.NextCode.ShouldBe(-1); api.NextMessage.ShouldBe((nuint)456); api.NextPointer.ShouldBe((nint)789);
            adapter.Metrics(1.5).ShouldBe(new ClickMetrics(500, 4, 4));
            api.MetricCalls.ToArray().ShouldBe(new[] { (36, 144u), (37, 144u) });
            adapter.PostQuit(api.ThreadId).ShouldBeTrue();
            api.QuitMessage.ShouldBe(0x12u);
            adapter.PumpMessage().ShouldBe(0);
            api.Calls.ShouldNotContain(c => c.Name == nameof(api.TranslateMessage) || c.Name == nameof(api.DispatchMessage));
        }
        finally { if (hook.Handle != 0) adapter.Unhook(hook).ShouldBeTrue(); }
        api.ActiveHandles.ShouldBe(0);
    }

    [Theory]
    [InlineData(0x200, 0, 0, 0)]
    [InlineData(0x201, 1, 1, 1)]
    [InlineData(0x202, 1, 0, 0)]
    [InlineData(0x204, 1, 1, 1)]
    [InlineData(0x205, 1, 0, 0)]
    [InlineData(0x207, 1, 1, 1)]
    [InlineData(0x208, 1, 0, 0)]
    [InlineData(0x20B, 1, 1, 1)]
    [InlineData(0x20C, 1, 0, 0)]
    [InlineData(0x20A, 0, 0, 0)]
    public void Snapshot_OnlyApprovedMetadataQueries(uint message, int foreground, int cursor, int shift)
    {
        using var api = new FakeMouseHookApi();
        var adapter = new NativeMouseAdapter(api);
        var raw = new MSLLHOOKSTRUCT { pt = new(int.MinValue, int.MaxValue), time = uint.MaxValue, flags = 3 };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(raw, pointer, false);
            var sample = adapter.Read(message, pointer, 900)!.Value;
            sample.Point.ShouldBe(new PixelPoint(int.MinValue, int.MaxValue));
            sample.Time.ShouldBe(uint.MaxValue); sample.Flags.ShouldBe(3u);
            api.Calls.Count(c => c.Name == nameof(api.ForegroundWindow)).ShouldBe(foreground);
            api.Calls.Count(c => c.Name == nameof(api.WindowThreadProcessId)).ShouldBe(foreground);
            api.Calls.Count(c => c.Name == nameof(api.GetCursorInfo)).ShouldBe(cursor);
            api.Calls.Count(c => c.Name == nameof(api.GetAsyncKeyState)).ShouldBe(shift);
            if (cursor != 0) api.CursorSize.ShouldBe((uint)Marshal.SizeOf<CURSORINFO>());
            if (shift != 0) api.ShiftKey.ShouldBe(0x10);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(-32768, true)]
    [InlineData(-32767, true)]
    public void Shift_UsesHighBitOnly(short value, bool expected)
    {
        using var api = new FakeMouseHookApi { Shift = value };
        ReadDown(api).Shift.ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, 1, 900, true)]
    [InlineData(false, 1, 900, false)]
    [InlineData(true, 0, 900, false)]
    [InlineData(true, 2, 900, false)]
    [InlineData(true, 1, 901, false)]
    public void Cursor_UnknownHiddenSuppressedAndCustomAreRejected(bool success, uint flags, int handle, bool expected)
    {
        using var api = new FakeMouseHookApi { CursorSuccess = success, CursorFlags = flags, CursorHandle = handle };
        ReadDown(api).IBeam.ShouldBe(expected);
    }

    [Fact]
    public void FailedPid_DoesNotRetainOutValue_AndUnknownMessageDoesNotReadPointer()
    {
        using var api = new FakeMouseHookApi { PidSuccess = false };
        ReadDown(api).Source.ProcessId.ShouldBe(0u);
        new NativeMouseAdapter(api).Read(0xffff, 0, 900).ShouldBeNull();
    }

    private static MouseSample ReadDown(FakeMouseHookApi api)
    {
        var adapter = new NativeMouseAdapter(api);
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try { Marshal.StructureToPtr(default(MSLLHOOKSTRUCT), pointer, false); return adapter.Read(0x201, pointer, 900)!.Value; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
