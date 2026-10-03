using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Hotkey;

public sealed class HotkeyCaptureServiceTests : IDisposable
{
    private sealed class FakeNativeKeyboardHookAdapter : INativeKeyboardHookAdapter
    {
        private const uint WM_QUIT = 0x0012;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;

        private readonly BlockingCollection<NativeMsg> _messageQueue = [];

        public nint ValidWindowHandle { get; set; } = 0x1000;
        public nint ForegroundWindowHandle { get; set; } = 0x1000;
        public uint CurrentProcessId { get; set; } = 5000;

        public int SetWindowsHookCallCount { get; set; }
        public int UnhookWindowsHookCallCount { get; set; }
        public int CallNextHookCallCount { get; set; }
        public int PostQuitCallCount { get; set; }
        public int EnsureQueueCallCount { get; set; }
        public int ReleaseResourcesCallCount { get; set; }

        public bool FailSetHook { get; set; }
        public bool FailUnhook { get; set; }
        public bool FailPostQuit { get; set; }
        public bool SimulateGetMessageError { get; set; }
        public int LastError { get; set; }
        public TaskCompletionSource? SetWindowsHookEnteredTcs { get; set; }
        public TaskCompletionSource? SetWindowsHookBlockTcs { get; set; }
        public bool ThrowOnDetectInitialHeldKeys { get; set; }
        public bool ThrowOnForegroundWindow { get; set; }

        public NativeKeyboardHookProc? CapturedHookProc { get; private set; }
        public HashSet<uint> InitialHeldKeys { get; } = [];

        public nint SetWindowsHook(NativeKeyboardHookProc proc)
        {
            SetWindowsHookCallCount++;
            CapturedHookProc = proc;
            SetWindowsHookEnteredTcs?.TrySetResult();
            if (SetWindowsHookBlockTcs != null)
            {
                SetWindowsHookBlockTcs.Task.GetAwaiter().GetResult();
            }
            if (FailSetHook)
            {
                LastError = 1400; // ERROR_INVALID_HOOK_HANDLE
                return 0;
            }
            return 0xABCD;
        }

        public bool UnhookWindowsHook(nint hookHandle)
        {
            UnhookWindowsHookCallCount++;
            if (FailUnhook)
            {
                LastError = 1404;
                return false;
            }
            return true;
        }

        public void ReleaseHookResources(nint hookHandle)
        {
            ReleaseResourcesCallCount++;
        }

        public void EnsureThreadMessageQueueCreated()
        {
            EnsureQueueCallCount++;
            while (_messageQueue.TryTake(out _)) { }
        }

        public nint CallNextHook(nint hookHandle, int nCode, nuint wParam, nint lParam)
        {
            CallNextHookCallCount++;
            return 0;
        }

        public uint? CustomWindowProcessId { get; set; }
        public nint GetForegroundWindow()
        {
            if (ThrowOnForegroundWindow)
            {
                throw new InvalidOperationException("Simulated GetForegroundWindow crash.");
            }
            return ForegroundWindowHandle;
        }
        public uint GetWindowProcessId(nint windowHandle) => CustomWindowProcessId ?? (windowHandle == ValidWindowHandle ? CurrentProcessId : 9999);
        public uint GetCurrentProcessId() => CurrentProcessId;
        public bool IsWindow(nint windowHandle) => windowHandle == ValidWindowHandle;
        public bool IsKeyDown(int virtualKey)
        {
            if (ThrowOnDetectInitialHeldKeys)
            {
                throw new InvalidOperationException("Simulated IsKeyDown failure.");
            }
            return InitialHeldKeys.Contains((uint)virtualKey);
        }
        public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;

        public bool PostThreadQuitMessage(uint threadId)
        {
            PostQuitCallCount++;
            if (FailPostQuit)
            {
                LastError = 1444; // ERROR_INVALID_THREAD_ID
                return false;
            }
            try
            {
                _messageQueue.TryAdd(new NativeMsg(WM_QUIT, 0, 0));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public int GetMessage(out NativeMsg msg)
        {
            if (SimulateGetMessageError)
            {
                LastError = 1400;
                msg = default;
                return -1;
            }
            try
            {
                msg = _messageQueue.Take();
                if (SimulateGetMessageError)
                {
                    LastError = 1400;
                    msg = default;
                    return -1;
                }
                return msg.Message == WM_QUIT ? 0 : 1;
            }
            catch (Exception)
            {
                msg = new NativeMsg(WM_QUIT, 0, 0);
                return 0;
            }
        }

        public int GetLastError() => LastError;

        public nint SendHookEvent(int nCode, uint msg, uint vk, bool isExtended = false)
        {
            if (CapturedHookProc == null)
            {
                throw new InvalidOperationException("Hook proc has not been registered.");
            }

            var kbd = new KbdLlHookStruct
            {
                vkCode = vk,
                scanCode = 0,
                flags = isExtended ? 1u : 0u,
                time = (uint)Environment.TickCount,
                dwExtraInfo = 0
            };

            nint pKbd = Marshal.AllocHGlobal(Marshal.SizeOf<KbdLlHookStruct>());
            try
            {
                Marshal.StructureToPtr(kbd, pKbd, false);
                return CapturedHookProc(nCode, (nuint)msg, pKbd);
            }
            finally
            {
                Marshal.FreeHGlobal(pKbd);
            }
        }

        public nint SendKeyDown(uint vk, bool isSystem = false, bool isExtended = false)
        {
            uint msg = isSystem ? WM_SYSKEYDOWN : WM_KEYDOWN;
            return SendHookEvent(0, msg, vk, isExtended);
        }

        public nint SendKeyUp(uint vk, bool isSystem = false, bool isExtended = false)
        {
            uint msg = isSystem ? WM_SYSKEYUP : WM_KEYUP;
            return SendHookEvent(0, msg, vk, isExtended);
        }

        public void Dispose()
        {
            try
            {
                _messageQueue.CompleteAdding();
            }
            catch
            {
            }
        }
    }

    private readonly FakeNativeKeyboardHookAdapter _adapter = new();
    private readonly HotkeyCaptureService _service;

    public HotkeyCaptureServiceTests()
    {
        _service = new HotkeyCaptureService(_adapter, null, NullLogger<HotkeyCaptureService>.Instance);
    }

    public void Dispose()
    {
        _adapter.Dispose();
    }

    [Fact]
    public void Constructor_DoesNotInvokeNativeOrStartThread()
    {
        _adapter.SetWindowsHookCallCount.ShouldBe(0);
        _adapter.UnhookWindowsHookCallCount.ShouldBe(0);
        _adapter.CapturedHookProc.ShouldBeNull();
    }

    [Fact]
    public async Task BeginAsync_WithInvalidHandleOrNonForeground_ThrowsInvalidArgument()
    {
        // 1. 句柄为 0
        var ex0 = await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(0, TestContext.Current.CancellationToken));
        ex0.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 2. 句柄不是有效窗口
        var exInvalid = await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(0x9999, TestContext.Current.CancellationToken));
        exInvalid.Code.ShouldBe(ErrorCode.InvalidArgument);

        // 3. 句柄属于其他进程
        _adapter.ValidWindowHandle = 0x2000;
        _adapter.ForegroundWindowHandle = 0x2000;
        _adapter.CurrentProcessId = 1111;
        _adapter.CustomWindowProcessId = 9999;
        var exProcess = await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(0x2000, TestContext.Current.CancellationToken));
        exProcess.Code.ShouldBe(ErrorCode.InvalidArgument);
        _adapter.CustomWindowProcessId = null;

        // 4. 窗口不是前台活动窗口
        _adapter.ValidWindowHandle = 0x1000;
        _adapter.ForegroundWindowHandle = 0x8888;
        var exNotForeground = await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(0x1000, TestContext.Current.CancellationToken));
        exNotForeground.Code.ShouldBe(ErrorCode.InvalidArgument);

        _adapter.SetWindowsHookCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task BeginAsync_WhenAnotherSessionActive_ThrowsConflict()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session.ShouldNotBeNull();

        var ex = await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.HotkeyConflict);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task BeginAsync_WhenSetHookFails_RollsBackAndSupportsRetry()
    {
        _adapter.FailSetHook = true;

        var ex = await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.Unknown);

        _adapter.SetWindowsHookCallCount.ShouldBe(1);

        // 重试成功
        _adapter.FailSetHook = false;
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session.ShouldNotBeNull();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task BeginAsync_CancellationAfterReturn_CancelsSubsequentKeyInputs()
    {
        using var cts = new CancellationTokenSource();
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, cts.Token);
        session.Current.IsReady.ShouldBeTrue();

        // Begin 返回后取消 token
        cts.Cancel();

        // 继续输入 Ctrl+F
        _adapter.SendKeyDown(0xA2); // LCtrl
        _adapter.SendKeyDown(0x46); // F
        _adapter.SendKeyUp(0x46);
        _adapter.SendKeyUp(0xA2);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Canceled);
        result.Binding.ShouldBeNull();

        await session.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_WhileSetWindowsHookInFlight_WaitsForInitializationAndCleansUpHook()
    {
        var hookEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _adapter.SetWindowsHookEnteredTcs = hookEnteredTcs;
        _adapter.SetWindowsHookBlockTcs = hookReleaseTcs;

        var beginTask = Task.Run(async () =>
        {
            return await _service.BeginAsync(_adapter.ValidWindowHandle, CancellationToken.None);
        });

        await hookEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // 调用 StopAsync，它不能提前成功，必须等待初始化并清理 hook
        var stopTask = _service.StopAsync();
        stopTask.IsCompleted.ShouldBeFalse("初始化尚未放行时 StopAsync 绝不能提前返回成功");

        // 放行 SetWindowsHook
        hookReleaseTcs.TrySetResult();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        _adapter.UnhookWindowsHookCallCount.ShouldBe(1);

        try
        {
            var s = await beginTask;
            await s.DisposeAsync();
        }
        catch
        {
        }
    }

    [Fact]
    public async Task BeginAsync_CanceledWhileSetWindowsHookInFlight_DoesNotLeakHookAndRollsBack()
    {
        var hookEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _adapter.SetWindowsHookEnteredTcs = hookEnteredTcs;
        _adapter.SetWindowsHookBlockTcs = hookReleaseTcs;

        using var cts = new CancellationTokenSource();
        var beginTask = Task.Run(async () =>
        {
            return await _service.BeginAsync(_adapter.ValidWindowHandle, cts.Token);
        });

        await hookEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // 在 SetWindowsHook 阻塞内部取消 token
        cts.Cancel();

        // 物理初始化未放行前，BeginAsync 绝不能提前退出
        beginTask.IsCompleted.ShouldBeFalse("物理初始化未放行前，BeginAsync 绝不能因取消提前返回");

        // 放行安装
        hookReleaseTcs.TrySetResult();

        await Should.ThrowAsync<OperationCanceledException>(async () => await beginTask);

        // 验证 hook 必须被回滚卸载，无残留
        _adapter.UnhookWindowsHookCallCount.ShouldBe(1);
        _adapter.ReleaseResourcesCallCount.ShouldBe(1);

        // 之后可以再次正常开启录入
        var session2 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session2.ShouldNotBeNull();
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task InitialHeldKeysReadThrows_HookIsStillUnhookedWithoutLeaking()
    {
        _adapter.ThrowOnDetectInitialHeldKeys = true;

        await Should.ThrowAsync<Exception>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));

        // 即使初始键读取异常，hookHandle 也已安全附加并成功卸载
        _adapter.UnhookWindowsHookCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task LowLevelKeyboardProc_ExceptionDoesNotLeakAcrossNativeBoundary()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        _adapter.ThrowOnForegroundWindow = true;

        // 发送按键事件，托管异常应被安全捕获转为 CallNextHook，绝不向 native 调用方抛出
        Should.NotThrow(() =>
        {
            var ret = _adapter.SendKeyDown(0x41);
            ret.ShouldBe(0);
        });

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_RequiresHeldKeysToReleaseBeforeBecomingReady()
    {
        // 模拟用户按下 Enter 启动录入，Enter (0x0D) 在启动瞬间处于按下状态
        _adapter.InitialHeldKeys.Add(0x0D);

        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session.Current.IsReady.ShouldBeFalse();

        // 尝试在准备阶段按下其他键，新按键进入等待释放集合，不应生成候选
        _adapter.SendKeyDown(0x41); // A
        session.Current.IsReady.ShouldBeFalse();
        session.Current.Key.ShouldBeNull();

        // 松开启动键 Enter，但 A 仍未释放，必须保持未 Ready
        _adapter.SendKeyUp(0x0D);
        session.Current.IsReady.ShouldBeFalse();

        // 松开 A，所有准备期按键全部释放，才变为 Ready
        _adapter.SendKeyUp(0x41);
        session.Current.IsReady.ShouldBeTrue();

        // 准备完成，此时按下组合键有效
        _adapter.SendKeyDown(0xA2); // LCtrl
        _adapter.SendKeyDown(0x41); // A
        session.Current.Modifiers.ShouldBe(HotkeyModifiers.Control);
        session.Current.Key.ShouldBe("A");

        // 释放完成
        _adapter.SendKeyUp(0x41);
        _adapter.SendKeyUp(0xA2);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Captured);
        result.Binding!.Key.ShouldBe("A");
        result.Binding.Modifiers.ShouldBe(HotkeyModifiers.Control);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_MergesLeftAndRightModifiers_AndSupportsWinKey()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // 按下左 Win (0x5B)、右 Ctrl (0xA3)、右 Shift (0xA1)
        _adapter.SendKeyDown(0x5B);
        _adapter.SendKeyDown(0xA3);
        _adapter.SendKeyDown(0xA1);

        session.Current.Modifiers.ShouldBe(HotkeyModifiers.Win | HotkeyModifiers.Control | HotkeyModifiers.Shift);

        // 按下主键 B (0x42)
        _adapter.SendKeyDown(0x42);
        session.Current.Key.ShouldBe("B");

        // 释放主键与修饰键
        _adapter.SendKeyUp(0x42);
        _adapter.SendKeyUp(0xA1);
        _adapter.SendKeyUp(0xA3);
        _adapter.SendKeyUp(0x5B);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Captured);
        result.Binding!.Modifiers.ShouldBe(HotkeyModifiers.Win | HotkeyModifiers.Control | HotkeyModifiers.Shift);
        result.Binding.Key.ShouldBe("B");
        result.Binding.Enabled.ShouldBeTrue();

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_CtrlDown_FDown_CtrlUp_FRepeat_FUp_RetainsControlSnapshotAndCompletesCaptured()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // Ctrl down
        _adapter.SendKeyDown(0xA2); // LCtrl
        // F down
        _adapter.SendKeyDown(0x46); // F
        session.Current.Modifiers.ShouldBe(HotkeyModifiers.Control);
        session.Current.Key.ShouldBe("F");

        // Ctrl up
        _adapter.SendKeyUp(0xA2);
        // 此时修饰键物理已释放，但候选已锁定
        session.Current.Modifiers.ShouldBe(HotkeyModifiers.Control);

        // F repeat (物理长按导致 repeated keydown)
        _adapter.SendKeyDown(0x46);
        session.Current.Modifiers.ShouldBe(HotkeyModifiers.Control);
        session.Current.Key.ShouldBe("F");

        // F up
        _adapter.SendKeyUp(0x46);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Captured);
        result.Binding!.Modifiers.ShouldBe(HotkeyModifiers.Control);
        result.Binding.Key.ShouldBe("F");

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_RejectsF12_ShiftLetter_AndSingleNonModifier()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // 1. 单独字母键 A（无修饰键）被拒绝
        _adapter.SendKeyDown(0x41);
        session.Current.Error.ShouldNotBeNull();
        session.Current.Error!.Code.ShouldBe(ErrorCode.HotkeyInvalid);
        _adapter.SendKeyUp(0x41);
        session.Completion.IsCompleted.ShouldBeFalse();

        // 2. F12 (0x7B) 被系统保留拒绝
        _adapter.SendKeyDown(0xA2); // Ctrl
        _adapter.SendKeyDown(0x7B); // F12
        session.Current.Error.ShouldNotBeNull();
        session.Current.Error!.Code.ShouldBe(ErrorCode.HotkeyInvalid);
        _adapter.SendKeyUp(0x7B);
        _adapter.SendKeyUp(0xA2);
        session.Completion.IsCompleted.ShouldBeFalse();

        // 3. 仅 Shift + 字母 A 被拒绝（避免冲突大写）
        _adapter.SendKeyDown(0xA0); // LShift
        _adapter.SendKeyDown(0x41); // A
        session.Current.Error.ShouldNotBeNull();
        session.Current.Error!.Code.ShouldBe(ErrorCode.HotkeyInvalid);
        _adapter.SendKeyUp(0x41);
        _adapter.SendKeyUp(0xA0);
        session.Completion.IsCompleted.ShouldBeFalse();

        // 4. 合法组合重录成功
        _adapter.SendKeyDown(0xA2); // Ctrl
        _adapter.SendKeyDown(0xA4); // Alt
        _adapter.SendKeyDown(0x41); // A
        _adapter.SendKeyUp(0x41);
        _adapter.SendKeyUp(0xA4);
        _adapter.SendKeyUp(0xA2);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Captured);
        result.Binding!.Key.ShouldBe("A");
        result.Binding.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_EscapeCancels_AndBackspaceClears()
    {
        // 1. 无修饰 Escape 取消
        var session1 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        _adapter.SendKeyDown(0x1B); // Escape
        _adapter.SendKeyUp(0x1B);

        var result1 = await session1.Completion;
        result1.Outcome.ShouldBe(HotkeyCaptureOutcome.Canceled);
        result1.Binding.ShouldBeNull();
        await session1.DisposeAsync();

        // 2. 无修饰 Backspace 清除
        var session2 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        _adapter.SendKeyDown(0x08); // Backspace
        _adapter.SendKeyUp(0x08);

        var result2 = await session2.Completion;
        result2.Outcome.ShouldBe(HotkeyCaptureOutcome.Cleared);
        result2.Binding.ShouldNotBeNull();
        result2.Binding!.Key.ShouldBe(string.Empty);
        result2.Binding.Enabled.ShouldBeFalse();
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task Capture_LosingForegroundCancelsCapture_AndDoesNotCaptureOtherWindowKeys()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // 模拟切换前台窗口到另一程序 (HWND 0x9999)
        _adapter.ForegroundWindowHandle = 0x9999;

        // 目标窗口外发生击键
        _adapter.SendKeyDown(0x41);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Canceled);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_LowLevelHook_NegativeNCodeCallsNextHook_AndExceptionsAreNotLeaked()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // nCode < 0 无条件透传
        var res = _adapter.SendHookEvent(-1, 0x0100, 0x41);
        _adapter.CallNextHookCallCount.ShouldBeGreaterThan(0);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_CancelsInFlightCapture_AndCompletesThreadExit()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        await _service.StopAsync();

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Canceled);
        _adapter.PostQuitCallCount.ShouldBeGreaterThan(0);
        _adapter.UnhookWindowsHookCallCount.ShouldBeGreaterThan(0);
        _adapter.ReleaseResourcesCallCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task UnhookFailure_PreservesCleanupOwnership_AndCanBeRetried()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        _adapter.FailUnhook = true;

        // 第一次 Dispose 抛出异常
        await Should.ThrowAsync<IrisException>(() => session.DisposeAsync().AsTask());

        // 再次尝试 BeginAsync 被阻止，因为清理失败仍持有
        await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));

        // 恢复 Unhook 并重试 StopAsync 成功
        _adapter.FailUnhook = false;
        await _service.StopAsync();

        // 此时可以重新开启新会话
        var session2 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session2.ShouldNotBeNull();
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task ProgressNotification_IsBoundedAndFiresSafely()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        int progressCount = 0;
        session.ProgressChanged += (_, _) =>
        {
            Interlocked.Increment(ref progressCount);
        };

        // 发送一系列按键
        _adapter.SendKeyDown(0xA2); // Ctrl
        _adapter.SendKeyDown(0xA4); // Alt
        _adapter.SendKeyDown(0x44); // D
        _adapter.SendKeyUp(0x44);
        _adapter.SendKeyUp(0xA4);
        _adapter.SendKeyUp(0xA2);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Captured);

        await Task.Delay(50, TestContext.Current.CancellationToken); // 等待通知分发
        progressCount.ShouldBeGreaterThan(0);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Capture_RejectsMultipleSimultaneousNonModifierKeys()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // 1. Ctrl down -> F down -> G down
        _adapter.SendKeyDown(0xA2); // LCtrl
        _adapter.SendKeyDown(0x46); // F
        session.Current.Key.ShouldBe("F");

        _adapter.SendKeyDown(0x47); // G (第二个非修饰键)
        session.Current.Error.ShouldNotBeNull();
        session.Current.Error!.Code.ShouldBe(ErrorCode.HotkeyInvalid);
        session.Current.Key.ShouldBeNull();

        // 2. 全部释放
        _adapter.SendKeyUp(0x46);
        _adapter.SendKeyUp(0x47);
        _adapter.SendKeyUp(0xA2);

        // 未完成，状态恢复且可以录入新组合
        session.Completion.IsCompleted.ShouldBeFalse();

        // 3. 重新输入合法组合 Ctrl + H
        _adapter.SendKeyDown(0xA2); // LCtrl
        _adapter.SendKeyDown(0x48); // H
        session.Current.Key.ShouldBe("H");

        _adapter.SendKeyUp(0x48);
        _adapter.SendKeyUp(0xA2);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Captured);
        result.Binding!.Key.ShouldBe("H");

        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PermanentlyDisablesBegin_AndSyncDisposeThrowsWhenActive()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // 1. 活跃资源存在时同步 Dispose 抛出异常
        var exSync = Should.Throw<InvalidOperationException>(() => _service.Dispose());
        exSync.Message.ShouldContain("禁止同步 Dispose");

        // 2. 正常异步 DisposeAsync 完成物理清理
        await _service.DisposeAsync();

        // 3. 处置后再次 BeginAsync 抛出 ObjectDisposedException
        await Should.ThrowAsync<ObjectDisposedException>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAsync_WhenPostQuitMessageFails_ThrowsAndSetsFailedCleanup_AndCanRetry()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        _adapter.FailPostQuit = true;

        // StopAsync 检查 bool 返回，失败应抛出异常并不死等
        await Should.ThrowAsync<IrisException>(() => _service.StopAsync());

        // 再次 BeginAsync 应当由于 cleanup 失败被阻止
        await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));

        // 恢复并重试 StopAsync，且不重复卸载已 unhook 的句柄
        _adapter.FailPostQuit = false;
        int unhookCountBefore = _adapter.UnhookWindowsHookCallCount;
        await _service.StopAsync();
        _adapter.UnhookWindowsHookCallCount.ShouldBe(unhookCountBefore, "重试时不应重复卸载已 unhook 的句柄");

        // 之后可以开启新会话
        var session2 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session2.ShouldNotBeNull();
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task MessageLoop_GetMessageError_FailsSessionAndCleansUp()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        _adapter.SimulateGetMessageError = true;
        _adapter.PostThreadQuitMessage(1); // 触发一次唤醒使 GetMessage 运行

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Failed);
        result.Binding.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error!.Message.ShouldContain("消息循环异常终止");

        // 验证 hook 已被清理卸载
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _adapter.UnhookWindowsHookCallCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task MessageLoop_UnexpectedLoopZero_BeforeStopRequested_FailsSessionAndCleansUp()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        // 未请求停止前收到 WM_QUIT (GetMessage 返回 0)
        _adapter.PostThreadQuitMessage(1);

        var result = await session.Completion;
        result.Outcome.ShouldBe(HotkeyCaptureOutcome.Failed);
        result.Binding.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error!.Message.ShouldContain("录入消息循环意外退出");

        // 验证 hook 已被清理卸载并等待完整收尾屏障
        await _service.StopAsync();
        _adapter.UnhookWindowsHookCallCount.ShouldBeGreaterThan(0);

        // 之后服务能够正常接纳新会话
        var session2 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);
        session2.ShouldNotBeNull();
        await session2.DisposeAsync();
    }

    [Fact]
    public async Task ServiceStopAsync_And_DisposeAsync_WaitForSlowProgressSubscriber_AndRejectsNewBeginWhileBlocked()
    {
        // 1. 测试 StopAsync 等待慢订阅者且期间拒绝新 BeginAsync
        var session1 = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        var subscriberEnteredTcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberReleaseTcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool subscriberFinished1 = false;

        session1.ProgressChanged += (_, _) =>
        {
            subscriberEnteredTcs1.TrySetResult();
            subscriberReleaseTcs1.Task.GetAwaiter().GetResult();
            subscriberFinished1 = true;
        };

        try
        {
            // 触发进度更新
            _adapter.SendKeyDown(0xA2); // LCtrl
            await subscriberEnteredTcs1.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 服务级 StopAsync: 此时 subscriber 正在阻塞，StopAsync 必须 pending
            var stopTask = _service.StopAsync();
            stopTask.IsCompleted.ShouldBeFalse("subscriber 阻塞期间服务 StopAsync 绝不能提前返回");

            // 期间新 BeginAsync 必须被拒绝
            await Should.ThrowAsync<IrisException>(() => _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken));
        }
        finally
        {
            // 放行 subscriber
            subscriberReleaseTcs1.TrySetResult();
        }

        await _service.StopAsync();
        subscriberFinished1.ShouldBeTrue();

        // 2. 释放后可以开始新会话，并测试 DisposeAsync 也等待慢订阅者
        var service2 = new HotkeyCaptureService(
            _adapter,
            null,
            NullLogger<HotkeyCaptureService>.Instance);

        var session2 = await service2.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        var subscriberEnteredTcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberReleaseTcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool subscriberFinished2 = false;

        session2.ProgressChanged += (_, _) =>
        {
            subscriberEnteredTcs2.TrySetResult();
            subscriberReleaseTcs2.Task.GetAwaiter().GetResult();
            subscriberFinished2 = true;
        };

        Task disposeTask;
        try
        {
            _adapter.SendKeyDown(0xA2); // LCtrl
            await subscriberEnteredTcs2.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            disposeTask = service2.DisposeAsync().AsTask();
            disposeTask.IsCompleted.ShouldBeFalse("subscriber 阻塞期间服务 DisposeAsync 绝不能提前返回");
        }
        finally
        {
            subscriberReleaseTcs2.TrySetResult();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        subscriberFinished2.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WaitsForSlowProgressSubscriberToComplete()
    {
        var session = await _service.BeginAsync(_adapter.ValidWindowHandle, TestContext.Current.CancellationToken);

        var subscriberEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool subscriberFinished = false;

        session.ProgressChanged += (_, _) =>
        {
            subscriberEnteredTcs.TrySetResult();
            subscriberReleaseTcs.Task.GetAwaiter().GetResult();
            subscriberFinished = true;
        };

        try
        {
            // 触发一次进度更新
            _adapter.SendKeyDown(0xA2); // LCtrl
            await subscriberEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 调用 session.DisposeAsync()，此时 subscriber 正在阻塞
            var disposeTask = session.DisposeAsync().AsTask();
            disposeTask.IsCompleted.ShouldBeFalse("subscriber 阻塞期间 DisposeAsync 绝不能提前返回");
        }
        finally
        {
            // 放行 subscriber
            subscriberReleaseTcs.TrySetResult();
        }

        await session.DisposeAsync();
        subscriberFinished.ShouldBeTrue();
    }
}
