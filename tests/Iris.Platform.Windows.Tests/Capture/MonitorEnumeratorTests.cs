using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Capture;

public sealed class MonitorEnumeratorTests
{
    private const uint ATTACHED = 0x00000001;
    private const uint MIRROR = 0x00000008;

    [Fact]
    public void EnumerateMonitors_AttachedAndNonMirror_RetainedAndSorted()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices =
        [
            new NativeDisplayDeviceData(@"\\.\DISPLAY2", ATTACHED),
            new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)
        ];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\DISPLAY2", 1920, 0, 3840, 1080, 1920, 0, 3840, 1040, false),
            new NativeMonitorEntry(2, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)
        ];

        adapter.WindowDpis[(nint)(0x10000 + (1920 + 960) * 31 + 540)] = 144; // 150% DPI
        adapter.WindowDpis[(nint)(0x10000 + 960 * 31 + 540)] = 96; // 100% DPI
        adapter.WindowMonitors[(nint)(0x10000 + (1920 + 960) * 31 + 540)] = 1;
        adapter.WindowMonitors[(nint)(0x10000 + 960 * 31 + 540)] = 2;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });

        monitors.Count.ShouldBe(2);
        // Primary monitor first
        monitors[0].DeviceName.ShouldBe(@"\\.\DISPLAY1");
        monitors[0].IsPrimary.ShouldBeTrue();
        monitors[0].DpiScale.ShouldBe(1.0);

        // Secondary monitor second
        monitors[1].DeviceName.ShouldBe(@"\\.\DISPLAY2");
        monitors[1].IsPrimary.ShouldBeFalse();
        monitors[1].DpiScale.ShouldBe(1.5);
    }

    [Fact]
    public void EnumerateMonitors_MirrorDriverAndNotAttached_FilteredOut()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices =
        [
            new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED),
            new NativeDisplayDeviceData(@"\\.\DISPLAY_MIRROR", ATTACHED | MIRROR), // Pseudo mirror
            new NativeDisplayDeviceData(@"\\.\DISPLAY_DETACHED", 0) // Detached
        ];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true),
            new NativeMonitorEntry(2, @"\\.\DISPLAY_MIRROR", 0, 0, 1920, 1080, 0, 0, 1920, 1080, false),
            new NativeMonitorEntry(3, @"\\.\DISPLAY_DETACHED", 0, 0, 1920, 1080, 0, 0, 1920, 1080, false)
        ];

        nint hwnd1 = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd1] = 1;
        adapter.WindowDpis[hwnd1] = 96;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });

        monitors.Count.ShouldBe(1);
        monitors[0].DeviceName.ShouldBe(@"\\.\DISPLAY1");
    }

    [Fact]
    public void EnumerateMonitors_AttachedRemoteDesktop_Retained()
    {
        var adapter = new FakeNativeDesktopAdapter();
        // Remote desktop session flag combined with ATTACHED
        uint remoteAttachedFlags = ATTACHED | 0x00000010; // Extra vendor/remote flag without MIRROR
        adapter.Devices =
        [
            new NativeDisplayDeviceData(@"\\.\RDP_DISPLAY", remoteAttachedFlags)
        ];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\RDP_DISPLAY", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)
        ];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 96;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });
        monitors.Count.ShouldBe(1);
        monitors[0].DeviceName.ShouldBe(@"\\.\RDP_DISPLAY");
    }

    [Fact]
    public void EnumerateMonitors_CaseInsensitiveDeviceNameMatching_Matches()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices =
        [
            new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED) // Uppercase in adapter
        ];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\display1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true) // Lowercase in monitor
        ];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 96;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });
        monitors.Count.ShouldBe(1);
        monitors[0].DeviceName.ShouldBe(@"\\.\display1");
    }

    [Fact]
    public void EnumerateMonitors_UnmappedMonitor_RetriesAndSucceedsOnSecondAttempt()
    {
        var customAdapter = new RetryingFakeAdapter(
            onFirstAttempt: (a) =>
            {
                a.Devices = []; // Empty device list -> inconsistent
            },
            onSecondAttempt: (a) =>
            {
                a.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
                a.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];
                nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
                a.WindowMonitors[hwnd] = 1;
                a.WindowDpis[hwnd] = 96;
            });

        var monitors = MonitorEnumerator.EnumerateMonitors(customAdapter, null, (_, _, _, _) => { });
        monitors.Count.ShouldBe(1);
        customAdapter.AttemptCount.ShouldBe(2, "Must retry exactly once upon inconsistent snapshot");
    }

    [Fact]
    public void EnumerateMonitors_ZeroDpi_TriggersRetry_AndThrowsWhenExhausted()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 0; // Invalid 0 DPI

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { }));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
    }

    [Fact]
    public void EnumerateMonitors_PrimaryFailure_PreservesPrimaryError_AndLogsCleanupFailures()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 0; // Primary failure: DPI = 0

        adapter.DestroyWindowFails = true; // Cleanup failure
        adapter.DestroyWindowError = 1400;

        var loggedCleanups = new List<(string Action, int ErrorCode)>();

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(
                adapter,
                null,
                (_, action, err, _) => loggedCleanups.Add((action, err))));

        // Primary error message is preserved!
        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
    }

    [Fact]
    public void EnumerateMonitors_CleanupFailureOnly_ThrowsCleanupFailureException()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 96;

        adapter.RestoreThreadDpiContextFails = true; // Only cleanup fails
        adapter.RestoreThreadDpiContextError = 1359;

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { }));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("桌面查询资源清理失败");
    }

    [Fact]
    public void EnumerateMonitors_ThreadDpiContext_AlwaysRestored()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.CurrentThreadDpiContext = 1001; // initial context
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 96;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });

        // Must be restored back to 1001
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
    }

    [Fact]
    public void EnumerateMonitors_EnumDisplayMonitors_RunsInsidePerMonitorV2Scope_AndRestoresOriginalContext()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.CurrentThreadDpiContext = 1001;
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 96;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });

        monitors.Count.ShouldBe(1);
        adapter.RecordedEnumContexts.ShouldNotBeEmpty();
        adapter.RecordedEnumContexts.All(c => c == -4).ShouldBeTrue("All device and monitor enum must execute under PerMonitorV2");
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
        adapter.SetThreadDpiContextTargets.ShouldBe([-4, 1001]);
    }

    [Fact]
    public void EnumerateMonitors_FirstQueryFails_FirstRestoreFails_TerminatesImmediately_PreservesPrimaryError_AndLogsCleanup()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.CurrentThreadDpiContext = 1001;
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        adapter.FailDpiOnce = true;
        adapter.FailRestoreOnce = true;

        var loggedCleanups = new List<(string Action, int ErrorCode)>();
        var testLogger = new TestLogger();

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(
                adapter,
                testLogger,
                (_, action, err, _) => loggedCleanups.Add((action, err))));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
        adapter.DpiQueryCallCount.ShouldBe(1, "Must terminate immediately without second attempt when cleanup fails");
        adapter.CurrentThreadDpiContext.ShouldBe(-4);
        loggedCleanups.Count.ShouldBe(1);
        loggedCleanups[0].Action.ShouldBe("SetThreadDpiAwarenessContext");
        loggedCleanups[0].ErrorCode.ShouldBe(1359);
        adapter.LiveWindows.Count.ShouldBe(0);
    }

    [Fact]
    public void EnumerateMonitors_FirstQueryFails_FirstDestroyFails_TerminatesImmediately_PreservesPrimaryError_AndLogsCleanup()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.CurrentThreadDpiContext = 1001;
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        adapter.FailDpiOnce = true;
        adapter.FailDestroyOnce = true;

        var loggedCleanups = new List<(string Action, int ErrorCode)>();
        var testLogger = new TestLogger();

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(
                adapter,
                testLogger,
                (_, action, err, _) => loggedCleanups.Add((action, err))));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
        adapter.DpiQueryCallCount.ShouldBe(1, "Must not retry when window destroy fails");
        loggedCleanups.Count.ShouldBe(1);
        loggedCleanups[0].Action.ShouldBe("DestroyWindow");
        loggedCleanups[0].ErrorCode.ShouldBe(1400);
        adapter.LiveWindows.Count.ShouldBe(1);
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
    }

    [Fact]
    public void EnumerateMonitors_FirstQueryFails_CleanCleanup_RetriesAndSucceedsOnSecondAttempt()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.CurrentThreadDpiContext = 1001;
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        adapter.FailDpiOnce = true; // Only query fails cleanly, cleanup succeeds

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });

        monitors.Count.ShouldBe(1);
        adapter.DpiQueryCallCount.ShouldBe(2, "Must retry exactly once on clean query failure");
        adapter.LiveWindows.Count.ShouldBe(0);
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
    }

    [Fact]
    public void EnumerateMonitors_ConflictingMonitorNames_DifferentGeometryOrHMonitor_RejectedAndThrows()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 100, 100, 0, 0, 100, 95, true),
            new NativeMonitorEntry(2, @"\\.\DISPLAY1", 100, 0, 200, 100, 100, 0, 200, 95, false)
        ];

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { }));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
        adapter.LiveWindows.Count.ShouldBe(0);
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
    }

    [Fact]
    public void EnumerateMonitors_ConflictingMonitorNames_CaseInsensitiveDifference_Rejected()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices =
        [
            new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)
        ];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 100, 100, 0, 0, 100, 95, true),
            new NativeMonitorEntry(2, @"\\.\display1", 100, 0, 200, 100, 100, 0, 200, 95, false) // Case difference + different bounds
        ];

        var ex = Should.Throw<IrisException>(() =>
            MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { }));

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取显示器信息");
        adapter.LiveWindows.Count.ShouldBe(0);
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
    }

    [Fact]
    public void EnumerateMonitors_ConflictingMonitorNames_FirstAttemptConflicted_SecondAttemptConsistent_Succeeds()
    {
        var customAdapter = new RetryingFakeAdapter(
            onFirstAttempt: (a) =>
            {
                a.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
                a.Monitors =
                [
                    new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 100, 100, 0, 0, 100, 95, true),
                    new NativeMonitorEntry(2, @"\\.\DISPLAY1", 100, 0, 200, 100, 100, 0, 200, 95, false)
                ];
            },
            onSecondAttempt: (a) =>
            {
                a.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
                a.Monitors =
                [
                    new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)
                ];
                nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
                a.WindowMonitors[hwnd] = 1;
                a.WindowDpis[hwnd] = 96;
            });

        var monitors = MonitorEnumerator.EnumerateMonitors(customAdapter, null, (_, _, _, _) => { });

        monitors.Count.ShouldBe(1);
        customAdapter.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public void EnumerateMonitors_IdenticalDuplicateMonitors_ExplicitlyDeduplicated()
    {
        var adapter = new FakeNativeDesktopAdapter();
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors =
        [
            new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true),
            new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true) // Identical duplicate
        ];

        nint hwnd = (nint)(0x10000 + 960 * 31 + 540);
        adapter.WindowMonitors[hwnd] = 1;
        adapter.WindowDpis[hwnd] = 96;

        var monitors = MonitorEnumerator.EnumerateMonitors(adapter, null, (_, _, _, _) => { });

        monitors.Count.ShouldBe(1, "Exact duplicate monitor entries must be deduplicated");
    }

    [Fact]
    public void EnumerateMonitors_QueryThrowsException_DestroyFails_PreservesPrimaryException_AndLogsCleanup()
    {
        var adapter = new ThrowingQueryFakeAdapter();
        adapter.Devices = [new NativeDisplayDeviceData(@"\\.\DISPLAY1", ATTACHED)];
        adapter.Monitors = [new NativeMonitorEntry(1, @"\\.\DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)];

        var loggedCleanups = new List<(string Action, int ErrorCode)>();
        var testLogger = new TestLogger();

        var ex = Should.Throw<InvalidOperationException>(() =>
            MonitorEnumerator.EnumerateMonitors(
                adapter,
                testLogger,
                (_, action, err, _) => loggedCleanups.Add((action, err))));

        ex.Message.ShouldBe("simulated query exception");
        loggedCleanups.Count.ShouldBe(1);
        loggedCleanups[0].Action.ShouldBe("DestroyWindow");
        loggedCleanups[0].ErrorCode.ShouldBe(1400);
        adapter.CurrentThreadDpiContext.ShouldBe(1001);
    }
}

internal sealed class TestLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

internal sealed class ThrowingQueryFakeAdapter : INativeDesktopAdapter
{
    public List<NativeDisplayDeviceData> Devices { get; set; } = [];
    public List<NativeMonitorEntry> Monitors { get; set; } = [];
    public nint CurrentThreadDpiContext { get; set; } = 1001;

    public bool EnumDisplayDevice(uint index, out NativeDisplayDeviceData device)
    {
        if (index < Devices.Count)
        {
            device = Devices[(int)index];
            return true;
        }
        device = default;
        return false;
    }

    public bool TryEnumDisplayMonitors(out IReadOnlyList<NativeMonitorEntry> monitors)
    {
        monitors = Monitors.ToArray();
        return true;
    }

    public nint SetThreadDpiAwarenessContext(nint dpiContext)
    {
        nint old = CurrentThreadDpiContext;
        CurrentThreadDpiContext = dpiContext;
        return old;
    }

    public nint CreateDpiQueryWindow(int x, int y) => 12345;

    public nint MonitorFromWindow(nint hwnd, uint flags) => throw new InvalidOperationException("simulated query exception");

    public uint GetDpiForWindow(nint hwnd) => 96;

    public bool DestroyWindow(nint hwnd) => false; // Fails to trigger cleanup failure

    public bool GetPhysicalCursorPos(out int x, out int y) { x = 0; y = 0; return true; }
    public nint GetForegroundWindow() => 0;
    public uint GetWindowThreadProcessId(nint hwnd, out uint processId) { processId = 0; return 0; }
    public nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId) => 0;
    public string? QueryFullProcessImageName(nint hProcess) => null;
    public bool CloseHandle(nint handle) => true;
    public string? GetWindowText(nint hwnd, int maxChars) => null;
    public uint GetCurrentProcessId() => 1;
    public uint GetCurrentThreadId() => 1;
    public int GetLastError() => 1400;
}

internal sealed class RetryingFakeAdapter : INativeDesktopAdapter
{
    private readonly Action<RetryingFakeAdapter> _onFirstAttempt;
    private readonly Action<RetryingFakeAdapter> _onSecondAttempt;
    public int AttemptCount { get; private set; }

    public List<NativeDisplayDeviceData> Devices { get; set; } = [];
    public List<NativeMonitorEntry> Monitors { get; set; } = [];
    public Dictionary<nint, uint> WindowDpis { get; } = new();
    public Dictionary<nint, nint> WindowMonitors { get; } = new();
    public nint CurrentThreadDpiContext { get; set; } = 1001;

    public RetryingFakeAdapter(Action<RetryingFakeAdapter> onFirstAttempt, Action<RetryingFakeAdapter> onSecondAttempt)
    {
        _onFirstAttempt = onFirstAttempt;
        _onSecondAttempt = onSecondAttempt;
    }

    public bool EnumDisplayDevice(uint index, out NativeDisplayDeviceData device)
    {
        if (index == 0)
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                _onFirstAttempt(this);
            }
            else
            {
                _onSecondAttempt(this);
            }
        }

        if (index < Devices.Count)
        {
            device = Devices[(int)index];
            return true;
        }

        device = default;
        return false;
    }

    public bool TryEnumDisplayMonitors(out IReadOnlyList<NativeMonitorEntry> monitors)
    {
        monitors = Monitors.ToArray();
        return true;
    }

    public nint SetThreadDpiAwarenessContext(nint dpiContext)
    {
        nint old = CurrentThreadDpiContext;
        CurrentThreadDpiContext = dpiContext;
        return old;
    }

    public nint CreateDpiQueryWindow(int x, int y) => (nint)(0x10000 + Math.Abs(x) * 31 + Math.Abs(y));

    public nint MonitorFromWindow(nint hwnd, uint flags)
    {
        if (WindowMonitors.TryGetValue(hwnd, out nint mon)) return mon;
        return Monitors.Count > 0 ? Monitors[0].HMonitor : 0;
    }

    public uint GetDpiForWindow(nint hwnd)
    {
        if (WindowDpis.TryGetValue(hwnd, out uint dpi)) return dpi;
        return 96;
    }

    public bool DestroyWindow(nint hwnd) => true;

    public bool GetPhysicalCursorPos(out int x, out int y) { x = 0; y = 0; return true; }

    public nint GetForegroundWindow() => 0;

    public uint GetWindowThreadProcessId(nint hwnd, out uint processId) { processId = 0; return 0; }

    public nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId) => 0;

    public string? QueryFullProcessImageName(nint hProcess) => null;

    public bool CloseHandle(nint handle) => true;

    public string? GetWindowText(nint hwnd, int maxChars) => null;

    public uint GetCurrentProcessId() => 1;

    public uint GetCurrentThreadId() => 1;

    public int GetLastError() => 0;
}
