using System;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Capture;

public sealed class DesktopContextTests
{
    [Fact]
    public void GetCursorPosition_Success_ReturnsPixelPoint()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            CursorX = 500,
            CursorY = 300,
            GetPhysicalCursorPosResult = true
        };

        var context = new DesktopContext(adapter, null);
        var pt = context.GetCursorPosition();

        pt.X.ShouldBe(500);
        pt.Y.ShouldBe(300);
    }

    [Fact]
    public void GetCursorPosition_Failure_ThrowsIrisException()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            GetPhysicalCursorPosResult = false
        };

        var context = new DesktopContext(adapter, null);
        var ex = Should.Throw<IrisException>(() => context.GetCursorPosition());

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("无法获取鼠标位置");
    }

    [Fact]
    public void GetForegroundApp_NoWindow_ReturnsNull()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 0
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldBeNull();
        adapter.CloseHandleCallCount.ShouldBe(0);
    }

    [Fact]
    public void GetForegroundApp_InvalidThreadOrPid_ReturnsNull()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 0,
            ForegroundThreadId = 0
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldBeNull();
        adapter.CloseHandleCallCount.ShouldBe(0);
    }

    [Fact]
    public void GetForegroundApp_OpenProcessAccessDenied_ReturnsNull()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 1234,
            ForegroundThreadId = 5678,
            OpenProcessFails = true
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldBeNull();
        adapter.CloseHandleCallCount.ShouldBe(0);
    }

    [Fact]
    public void GetForegroundApp_ExtractsProcessNameAndTitle_ClosesHandle()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 1234,
            ForegroundThreadId = 5678,
            ProcessImagePath = @"C:\Program Files\App\editor.exe",
            WindowTextResult = "Document 1"
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldNotBeNull();
        app.ProcessName.ShouldBe("editor.exe");
        app.ProcessId.ShouldBe(1234);
        app.WindowTitle.ShouldBe("Document 1");
        app.WindowHandle.ShouldBe(100);

        adapter.CloseHandleCallCount.ShouldBe(1);
    }

    [Fact]
    public void GetForegroundApp_WindowSwitchedDuringQuery_RetriesAndReturnsNullIfStillUnstable()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 1234,
            ForegroundThreadId = 5678,
            ProcessImagePath = @"C:\Windows\explorer.exe",
            ReSampleForegroundSwitches = true,
            SwitchedForegroundWindowHandle = 200,
            SwitchedForegroundProcessId = 9999
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        // Failed both attempts due to switching
        app.ShouldBeNull();
        adapter.GetForegroundWindowCallCount.ShouldBe(4); // 2 calls per attempt * 2 attempts = 4
        adapter.CloseHandleCallCount.ShouldBe(2); // closed handle in both attempts
    }

    [Fact]
    public void GetForegroundApp_LongTitle_TruncatedTo1024Chars()
    {
        string longTitle = new string('A', 2000);
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 1234,
            ForegroundThreadId = 5678,
            ProcessImagePath = @"C:\Test\test.exe",
            WindowTextResult = longTitle
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldNotBeNull();
        app.WindowTitle.ShouldNotBeNull();
        app.WindowTitle.Length.ShouldBe(1024);
        app.WindowTitle.ShouldBe(new string('A', 1024));
    }

    [Fact]
    public void GetForegroundApp_SameProcessDifferentThread_ReturnsNullTitle_AvoidsCrossThreadWait()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 5000,
            ForegroundThreadId = 6001,
            CurrentProcessIdValue = 5000, // Same process
            CurrentThreadIdValue = 6002,  // Different thread
            ProcessImagePath = @"C:\Iris\Iris.exe",
            WindowTextResult = "Main Window"
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldNotBeNull();
        app.ProcessName.ShouldBe("Iris.exe");
        app.WindowTitle.ShouldBeNull("Same-process cross-thread window must have null WindowTitle to prevent WM_GETTEXT hang");
    }

    [Fact]
    public void GetForegroundApp_SameProcessSameThread_AllowsTitle()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 5000,
            ForegroundThreadId = 6001,
            CurrentProcessIdValue = 5000, // Same process
            CurrentThreadIdValue = 6001,  // Same thread
            ProcessImagePath = @"C:\Iris\Iris.exe",
            WindowTextResult = "Main Window"
        };

        var context = new DesktopContext(adapter, null);
        var app = context.GetForegroundApp();

        app.ShouldNotBeNull();
        app.ProcessName.ShouldBe("Iris.exe");
        app.WindowTitle.ShouldBe("Main Window");
    }

    [Fact]
    public void GetForegroundApp_CloseHandleFails_ThrowsCleanupFailureException()
    {
        var adapter = new FakeNativeDesktopAdapter
        {
            ForegroundWindowHandle = 100,
            ForegroundProcessId = 1234,
            ForegroundThreadId = 5678,
            ProcessImagePath = @"C:\Test\test.exe",
            CloseHandleFails = true,
            CloseHandleError = 6
        };

        var context = new DesktopContext(adapter, null);
        var ex = Should.Throw<IrisException>(() => context.GetForegroundApp());

        ex.Code.ShouldBe(ErrorCode.Unknown);
        ex.Message.ShouldBe("桌面查询资源清理失败");
    }
}
