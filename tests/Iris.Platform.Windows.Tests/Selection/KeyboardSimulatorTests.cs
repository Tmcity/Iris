using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Platform.Windows.Selection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class KeyboardSimulatorTests : IDisposable
{
    private readonly FakeNativeKeyboardInputAdapter _fakeNative = new();
    private readonly TrackingFakeTimeProvider _fakeTime = new();

    public void Dispose()
    {
        _fakeTime.Dispose();
    }

    private KeyboardSimulator CreateSimulator()
    {
        return new KeyboardSimulator(_fakeNative, _fakeTime);
    }

    #region 1. 构造、DI 与目标参数校验

    [Fact]
    public void Constructor_DoesNotCallAnyNativeMethods()
    {
        var simulator = CreateSimulator();
        simulator.ShouldNotBeNull();
        _fakeNative.TotalNativeCalls.ShouldBe(0);
    }

    [Fact]
    public void DI_Resolution_AddIrisPlatformWindows_ResolvesSimulatorAndAdapter_ZeroNativeCalls()
    {
        var services = new ServiceCollection();
        services.AddIrisPlatformWindows();
        using var sp = services.BuildServiceProvider();

        var adapter = sp.GetRequiredService<INativeKeyboardInputAdapter>();
        var simulator = sp.GetRequiredService<KeyboardSimulator>();

        adapter.ShouldNotBeNull();
        simulator.ShouldNotBeNull();

        // 验证单例注入
        var adapter2 = sp.GetRequiredService<INativeKeyboardInputAdapter>();
        var simulator2 = sp.GetRequiredService<KeyboardSimulator>();
        ReferenceEquals(adapter, adapter2).ShouldBeTrue();
        ReferenceEquals(simulator, simulator2).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 5000)]
    [InlineData(0x1234, 0)]
    [InlineData(0, 0)]
    public async Task TargetValidation_ZeroHwndOrPid_ThrowsIrisException_InvalidArgument(nint hwnd, uint pid)
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(hwnd, pid);

        var ex = await Should.ThrowAsync<IrisException>(() => simulator.CopyAsync(target, TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.InvalidArgument);
        ex.Message.ShouldContain("non-zero");
        _fakeNative.RecordedInputs.Count.ShouldBe(0);
    }

    [Fact]
    public async Task TargetValidation_CurrentProcess_ReturnsTargetChanged_ZeroInputs()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, _fakeNative.CurrentProcessId);

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.TargetChanged);
        result.MayHaveCopied.ShouldBeFalse();
        _fakeNative.RecordedInputs.Count.ShouldBe(0);
    }

    [Fact]
    public async Task TargetValidation_ForegroundWindowMismatch_ReturnsTargetChanged_ZeroInputs()
    {
        var simulator = CreateSimulator();
        _fakeNative.ForegroundWindow = 0x9999; // 不同于 target 的 0x1234
        var target = new KeyboardCopyTarget(0x1234, 5000);

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.TargetChanged);
        result.MayHaveCopied.ShouldBeFalse();
        _fakeNative.RecordedInputs.Count.ShouldBe(0);
    }

    [Fact]
    public async Task TargetValidation_InvalidWindow_ReturnsTargetChanged_ZeroInputs()
    {
        var simulator = CreateSimulator();
        _fakeNative.WindowIsValid = false;
        var target = new KeyboardCopyTarget(0x1234, 5000);

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.TargetChanged);
        result.MayHaveCopied.ShouldBeFalse();
        _fakeNative.RecordedInputs.Count.ShouldBe(0);
    }

    #endregion

    #region 2. 修饰键释放等待与 500ms 超时边界

    [Fact]
    public async Task Modifiers_InitiallyReleased_ExecutesImmediately()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
        result.MayHaveCopied.ShouldBeTrue();
        result.Error.ShouldBeNull();
        _fakeNative.RecordedInputs.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Modifiers_Held_ReleasedBefore500ms_ExecutesSuccessfully()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        // 模拟用户仍按住左 Ctrl (0xA2)
        _fakeNative.KeyStates[0xA2] = unchecked((short)0x8000);

        var copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        // 前 10ms 仍按住：确认计时器已进入并推进 10ms
        await _fakeTime.WaitForTimerCreatedAsync(TestContext.Current.CancellationToken);
        _fakeTime.Advance(TimeSpan.FromMilliseconds(10));
        _fakeNative.RecordedInputs.Count.ShouldBe(0);

        // 进入第二个 10ms 延迟
        await _fakeTime.WaitForTimerCreatedAsync(TestContext.Current.CancellationToken);

        // 在第二个延迟期间，用户释放了左 Ctrl
        _fakeNative.KeyStates[0xA2] = 0;

        // 推进时间，Timer 到期唤醒并检测到按键已释放
        _fakeTime.Advance(TimeSpan.FromMilliseconds(10));

        var result = await copyTask;
        result.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
        result.MayHaveCopied.ShouldBeTrue();
        _fakeNative.RecordedInputs.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Modifiers_HeldExceeding500ms_ReturnsNotReady_ZeroInputs_NoUserKeyUpInjected()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        // 模拟用户持续按住左 Alt (0xA4)
        _fakeNative.KeyStates[0xA4] = unchecked((short)0x8000);

        var copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        // 逐步推进超过 500ms（每次循环由计时器触发推进 10ms，50 次达到 500ms 超时）
        for (int i = 0; i < 50; i++)
        {
            await _fakeTime.WaitForTimerCreatedAsync(TestContext.Current.CancellationToken);
            _fakeTime.Advance(TimeSpan.FromMilliseconds(10));
        }

        var result = await copyTask;

        // 严格遵循 ARCH v1.30 与 KEYBOARD_SIMULATION.md：超时返回 NotReady，零输入，绝不强发用户按键的 KeyUp！
        result.Outcome.ShouldBe(KeyboardCopyOutcome.NotReady);
        result.MayHaveCopied.ShouldBeFalse();
        result.Error.ShouldBeNull();
        _fakeNative.RecordedInputs.Count.ShouldBe(0, "超时后严禁向系统注入任何按键事件，不可强行释放用户按键");
    }

    [Fact]
    public async Task Cancellation_BeforeSendInput_ThrowsOperationCanceledException_ZeroInputs()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        // 按住 C 键使其处于轮询等待中
        _fakeNative.KeyStates[0x43] = unchecked((short)0x8000);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var copyTask = simulator.CopyAsync(target, cts.Token);

        await _fakeTime.WaitForTimerCreatedAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => copyTask);
        _fakeNative.RecordedInputs.Count.ShouldBe(0, "在发送前取消严禁产生任何输入");
    }

    [Fact]
    public async Task TargetChanged_DuringModifierWait_ReturnsTargetChanged_ZeroInputs()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.KeyStates[0xA0] = unchecked((short)0x8000); // Shift held

        var copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        // 进入延迟等待
        await _fakeTime.WaitForTimerCreatedAsync(TestContext.Current.CancellationToken);

        // 在等待期间目标切换
        _fakeNative.ForegroundWindow = 0x8888;

        // 推进时间，Delay 唤醒后检测到目标已切换
        _fakeTime.Advance(TimeSpan.FromMilliseconds(10));

        var result = await copyTask;
        result.Outcome.ShouldBe(KeyboardCopyOutcome.TargetChanged);
        result.MayHaveCopied.ShouldBeFalse();
        _fakeNative.RecordedInputs.Count.ShouldBe(0);
    }

    #endregion

    #region 3. INPUT 结构体构造与完整单事件确认序列

    [Fact]
    public async Task NormalCopy_ProducesExactFourInputsSequence_WithCorrectABIFields()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
        result.MayHaveCopied.ShouldBeTrue();
        result.Error.ShouldBeNull();

        _fakeNative.RecordedInputs.Count.ShouldBe(4);

        int expectedCbSize = Marshal.SizeOf<INPUT>();

        // 1. LCtrl down (0xA2)
        var input0 = _fakeNative.RecordedInputs[0];
        input0.CInputs.ShouldBe(1u);
        input0.CbSize.ShouldBe(expectedCbSize);
        input0.Input.type.ShouldBe(INPUT_TYPE.INPUT_KEYBOARD);
        ((ushort)input0.Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        input0.Input.Anonymous.ki.dwFlags.ShouldBe((KEYBD_EVENT_FLAGS)0);
        input0.Input.Anonymous.ki.wScan.ShouldBe((ushort)0);
        input0.Input.Anonymous.ki.time.ShouldBe(0u);
        input0.Input.Anonymous.ki.dwExtraInfo.ShouldBe((nuint)0);

        // 2. C down (0x43)
        var input1 = _fakeNative.RecordedInputs[1];
        input1.CInputs.ShouldBe(1u);
        input1.CbSize.ShouldBe(expectedCbSize);
        input1.Input.type.ShouldBe(INPUT_TYPE.INPUT_KEYBOARD);
        ((ushort)input1.Input.Anonymous.ki.wVk).ShouldBe((ushort)0x43);
        input1.Input.Anonymous.ki.dwFlags.ShouldBe((KEYBD_EVENT_FLAGS)0);

        // 3. C up (0x43)
        var input2 = _fakeNative.RecordedInputs[2];
        input2.CInputs.ShouldBe(1u);
        input2.CbSize.ShouldBe(expectedCbSize);
        input2.Input.type.ShouldBe(INPUT_TYPE.INPUT_KEYBOARD);
        ((ushort)input2.Input.Anonymous.ki.wVk).ShouldBe((ushort)0x43);
        input2.Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);

        // 4. LCtrl up (0xA2)
        var input3 = _fakeNative.RecordedInputs[3];
        input3.CInputs.ShouldBe(1u);
        input3.CbSize.ShouldBe(expectedCbSize);
        input3.Input.type.ShouldBe(INPUT_TYPE.INPUT_KEYBOARD);
        ((ushort)input3.Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        input3.Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);

        simulator.HasPendingRelease.ShouldBeFalse();
    }

    #endregion

    #region 4. 中途目标切换

    [Fact]
    public async Task TargetChanged_BeforeCDown_AbortsFurtherKeyDown_ReleasesCtrl_ReturnsTargetChanged_MayHaveCopiedFalse()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        // 在 LCtrl down 确认后、C down 发送前，前台切换
        _fakeNative.CustomSendInputHandler = (cInputs, inputs, cbSize) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0xA2 && flags == 0) // LCtrl down
            {
                // 模拟切出前台
                _fakeNative.ForegroundWindow = 0x9999;
                return 1;
            }

            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.TargetChanged);
        result.MayHaveCopied.ShouldBeFalse("C down 未执行，复制不可发生");

        // 验证只发送了 LCtrl down 与 LCtrl up，绝未发送 C down
        _fakeNative.RecordedInputs.Count.ShouldBe(2);
        ((ushort)_fakeNative.RecordedInputs[0].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[0].Input.Anonymous.ki.dwFlags.ShouldBe((KEYBD_EVENT_FLAGS)0);

        ((ushort)_fakeNative.RecordedInputs[1].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[1].Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);

        simulator.HasPendingRelease.ShouldBeFalse();
    }

    #endregion

    #region 5. 原生失败与反向有界补偿

    [Fact]
    public async Task LCtrlDown_Returns0_NoCompensationNeeded_ReturnsFailed_MayHaveCopiedFalse()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) => 0; // 模拟被系统拒绝
        _fakeNative.LastErrorCode = 5; // Access denied

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Detail.ShouldNotBeNull();
        result.Error.Detail.ShouldContain("5");

        // 仅尝试了 1 次 LCtrl down，没有后续输入或补偿
        _fakeNative.RecordedInputs.Count.ShouldBe(1);
        simulator.HasPendingRelease.ShouldBeFalse();
    }

    [Fact]
    public async Task LCtrlDown_ThrowsException_ConservativelyRetainsPending_CompensatesCtrlUp_ReturnsFailed()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        int callCount = 0;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("Native crash simulation");
            }
            return 1; // 补偿时成功
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeFalse();
        result.Error.ShouldNotBeNull();

        // 第一次 LCtrl down 异常，第二次 LCtrl up 补偿成功
        _fakeNative.RecordedInputs.Count.ShouldBe(2);
        ((ushort)_fakeNative.RecordedInputs[1].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[1].Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);

        simulator.HasPendingRelease.ShouldBeFalse();
    }

    [Fact]
    public async Task CDown_Returns0_CompensatesCtrlUp_ReturnsFailed_MayHaveCopiedFalse()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0x43 && flags == 0) // C down 失败
            {
                return 0;
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeFalse("C down 返回 0 明确未被输入流接纳");

        // 序列：LCtrl down (1), C down (0), LCtrl up (1)
        _fakeNative.RecordedInputs.Count.ShouldBe(3);
        ((ushort)_fakeNative.RecordedInputs[0].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        ((ushort)_fakeNative.RecordedInputs[1].Input.Anonymous.ki.wVk).ShouldBe((ushort)0x43);
        ((ushort)_fakeNative.RecordedInputs[2].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[2].Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);

        simulator.HasPendingRelease.ShouldBeFalse();
    }

    [Fact]
    public async Task CDown_ThrowsException_RetainsC_MayHaveCopiedTrue_CompensatesReverseOrder()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0x43 && flags == 0)
            {
                throw new InvalidOperationException("P/Invoke fault");
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeTrue("C down 结果无法确认时保守记录 MayHaveCopied=true");

        // 补偿按反向顺序：C up 然后 LCtrl up
        _fakeNative.RecordedInputs.Count.ShouldBe(4);
        // 0: LCtrl down
        ((ushort)_fakeNative.RecordedInputs[0].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        // 1: C down (抛错)
        ((ushort)_fakeNative.RecordedInputs[1].Input.Anonymous.ki.wVk).ShouldBe((ushort)0x43);
        // 2: C up 补偿
        ((ushort)_fakeNative.RecordedInputs[2].Input.Anonymous.ki.wVk).ShouldBe((ushort)0x43);
        _fakeNative.RecordedInputs[2].Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
        // 3: LCtrl up 补偿
        ((ushort)_fakeNative.RecordedInputs[3].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[3].Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);

        simulator.HasPendingRelease.ShouldBeFalse();
    }

    [Fact]
    public async Task CUp_Returns0_RetainsC_CompensatesCtrlUp_ReturnsFailed_HasPendingReleaseTrue()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0; // C up 失败（正常与补偿均失败）
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeTrue();

        // 对应规范：Dctrl(1), Dc(1), Uc(0), Uc(0), Uctrl(1)
        _fakeNative.RecordedInputs.Count.ShouldBe(5);
        simulator.HasPendingRelease.ShouldBeTrue("C up 补偿仍失败必须保留释放责任");

        // 后续调用立即返回 Busy
        var busyResult = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        busyResult.Outcome.ShouldBe(KeyboardCopyOutcome.Busy);
    }

    [Fact]
    public async Task CUp_FailsInitially_CompensationSucceeds_ReturnsFailed_NoPendingResidual()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        int cUpCount = 0;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                cUpCount++;
                if (cUpCount == 1)
                {
                    return 0; // 第一次 C up 失败
                }
                return 1; // 补偿时 C up 成功
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeTrue();

        // 对应规范：Dctrl(1), Dc(1), Uc(0), Uc(1), Uctrl(1)
        _fakeNative.RecordedInputs.Count.ShouldBe(5);
        simulator.HasPendingRelease.ShouldBeFalse("补偿成功后不残留释放责任");
    }

    [Fact]
    public async Task LCtrlUp_CompensationAlsoFails_RetainsCtrl_ReturnsFailed_HasPendingReleaseTrue()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0xA2 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0; // LCtrl up 失败（正常与补偿均失败）
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeTrue();

        // 对应规范：Dctrl(1), Dc(1), Uc(1), Uctrl(0), Uctrl(0)
        _fakeNative.RecordedInputs.Count.ShouldBe(5);
        simulator.HasPendingRelease.ShouldBeTrue();

        // 再次调用被 Busy 拒绝
        var busyResult = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        busyResult.Outcome.ShouldBe(KeyboardCopyOutcome.Busy);
    }

    [Fact]
    public async Task LCtrlUp_FailsInitially_CompensationSucceeds_ReturnsFailed_NoPendingResidual()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        int ctrlUpCount = 0;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (vk == 0xA2 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                ctrlUpCount++;
                if (ctrlUpCount == 1)
                {
                    return 0; // 第一次 LCtrl up 失败
                }
                return 1; // 补偿时 LCtrl up 成功
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        result.MayHaveCopied.ShouldBeTrue();

        // 对应规范：Dctrl(1), Dc(1), Uc(1), Uctrl(0), Uctrl(1)
        _fakeNative.RecordedInputs.Count.ShouldBe(5);
        simulator.HasPendingRelease.ShouldBeFalse();
    }

    #endregion

    #region 6. 补偿重试、并发 Busy、Stop 与生命周期

    [Fact]
    public async Task CompensationFails_PreservesPendingRelease_RetryCleanupSucceeds_SubsequentCopyCanProceed()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        bool failCUp = true;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;

            if (failCUp && vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0;
            }
            return 1;
        };

        var result1 = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result1.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeTrue();

        // 修复环境，执行重试清理
        failCUp = false;
        await simulator.RetryCleanupAsync();

        simulator.HasPendingRelease.ShouldBeFalse("重试成功后应清空待释放键集合");

        // 再次执行 Copy 可以正常接纳并成功
        var result2 = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result2.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
        result2.MayHaveCopied.ShouldBeTrue();
    }

    [Fact]
    public async Task RetryCleanup_WhenNoPendingRelease_IsIdempotent_ZeroNativeCalls()
    {
        var simulator = CreateSimulator();
        simulator.HasPendingRelease.ShouldBeFalse();

        int initialCalls = _fakeNative.TotalNativeCalls;
        await simulator.RetryCleanupAsync();

        _fakeNative.TotalNativeCalls.ShouldBe(initialCalls, "无待释放项时 RetryCleanup 绝不产生任何 native 调用");
    }

    [Fact]
    public async Task Busy_ConcurrentCopy_RejectedImmediatelyWithoutQueueing()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.SendInputEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeNative.SendInputReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<KeyboardCopyResult>? copyTask1 = null;
        try
        {
            copyTask1 = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

            // 等待第一个调用进入 SendInput 原生阻塞点
            await _fakeNative.SendInputEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 第二个并发调用必须被立即以 Busy 拒绝
            var copyResult2 = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
            copyResult2.Outcome.ShouldBe(KeyboardCopyOutcome.Busy);

            // 放行第一个调用
            _fakeNative.SendInputReleaseTcs.TrySetResult();

            var copyResult1 = await copyTask1;
            copyResult1.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
        }
        finally
        {
            _fakeNative.SendInputReleaseTcs?.TrySetResult();
            if (copyTask1 != null)
            {
                try { await copyTask1; } catch { }
            }
        }
    }

    [Fact]
    public async Task StopAsync_PermanentlyRejectsSubsequentCopy_WithInvalidOperationException()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        await simulator.StopAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => simulator.CopyAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAsync_CancelsModifierWait_AndCompletesCleanly()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.KeyStates[0xA2] = unchecked((short)0x8000); // Ctrl held

        var copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        // 等待进入延迟
        await _fakeTime.WaitForTimerCreatedAsync(TestContext.Current.CancellationToken);

        // 在等待期间停止
        var stopTask = simulator.StopAsync();

        await stopTask;
        await Should.ThrowAsync<OperationCanceledException>(() => copyTask);

        _fakeNative.RecordedInputs.Count.ShouldBe(0);
        simulator.HasPendingRelease.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_AwaitsInFlightNativeWork_ThenCleansUpPendingKeys()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.SendInputEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeNative.SendInputReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<KeyboardCopyResult>? copyTask = null;
        Task? stopTask = null;
        try
        {
            copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

            // 等待进入 LCtrl down
            await _fakeNative.SendInputEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 发起 StopAsync
            stopTask = simulator.StopAsync();
            stopTask.IsCompleted.ShouldBeFalse("StopAsync 必须等待在途活动工作完成");

            // 放行并让其完成
            _fakeNative.SendInputReleaseTcs.TrySetResult();

            await copyTask;
            await stopTask;

            simulator.HasPendingRelease.ShouldBeFalse();
        }
        finally
        {
            _fakeNative.SendInputReleaseTcs?.TrySetResult();
            if (copyTask != null)
            {
                try { await copyTask; } catch { }
            }
            if (stopTask != null)
            {
                try { await stopTask; } catch { }
            }
        }
    }

    [Fact]
    public void SynchronousDispose_WhenIdle_Succeeds()
    {
        var simulator = CreateSimulator();
        simulator.Dispose(); // 成功无异常

        var target = new KeyboardCopyTarget(0x1234, 5000);
        Should.Throw<ObjectDisposedException>(() => simulator.CopyAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SynchronousDispose_WhenActiveOperationOrPendingRelease_ThrowsInvalidOperationException()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.SendInputEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeNative.SendInputReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<KeyboardCopyResult>? copyTask = null;
        try
        {
            copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);
            await _fakeNative.SendInputEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 活动期间同步 Dispose 绝不可阻塞 UI，必须明确抛出 InvalidOperationException
            Should.Throw<InvalidOperationException>(() => simulator.Dispose());

            _fakeNative.SendInputReleaseTcs.TrySetResult();
            await copyTask;
        }
        finally
        {
            _fakeNative.SendInputReleaseTcs?.TrySetResult();
            if (copyTask != null)
            {
                try { await copyTask; } catch { }
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_AwaitsStopAsync_AndMarksDisposed()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        await simulator.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => simulator.CopyAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetryCleanupAsync_WhenCompensateFails_ThrowsIrisException_RetainsPendingRelease()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        bool failCUp = true;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;
            if (failCUp && vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0;
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeTrue();

        var ex = await Should.ThrowAsync<IrisException>(() => simulator.RetryCleanupAsync());
        ex.Message.ShouldContain("清理失败");
        simulator.HasPendingRelease.ShouldBeTrue("清理失败必须保留待释放按键责任");

        failCUp = false;
        await simulator.RetryCleanupAsync();
        simulator.HasPendingRelease.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenCleanupFails_ThrowsIrisException_RetainsPendingRelease_AllowsRetry()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        bool failCUp = true;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;
            if (failCUp && vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0;
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeTrue();

        await Should.ThrowAsync<IrisException>(() => simulator.StopAsync());
        simulator.HasPendingRelease.ShouldBeTrue("StopAsync 清理失败必须保留按键释放责任");

        await Should.ThrowAsync<InvalidOperationException>(() => simulator.CopyAsync(target, TestContext.Current.CancellationToken));

        failCUp = false;
        await simulator.StopAsync();
        simulator.HasPendingRelease.ShouldBeFalse("重试 StopAsync 成功清理残留按键");
    }

    [Fact]
    public async Task DisposeAsync_WhenCleanupFails_ThrowsAndDoesNotMarkDisposed()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;
            if (vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0;
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeTrue();

        await Should.ThrowAsync<IrisException>(async () => await simulator.DisposeAsync());
        simulator.HasPendingRelease.ShouldBeTrue("带 pending keys 失败后必须保留责任，不能标为已 disposed");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => simulator.CopyAsync(target, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("stopped");
    }

    [Fact]
    public async Task SynchronousDispose_WhenPendingKeysExist_ThrowsInvalidOperationException_DoesNotMarkDisposed()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;
            if (vk == 0x43 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
            {
                return 0;
            }
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => simulator.Dispose());
        simulator.HasPendingRelease.ShouldBeTrue();
    }

    [Fact]
    public async Task LCtrlDown_Succeeds_ThenIsTargetValidThrows_CompensatesCtrl_ReturnsFailed()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        bool lctrlDownHappened = false;
        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;
            if (vk == 0xA2 && flags == 0)
            {
                lctrlDownHappened = true;
            }
            return 1;
        };

        _fakeNative.CustomGetForegroundWindowHandler = () =>
        {
            if (lctrlDownHappened)
            {
                throw new InvalidOperationException("Simulated Win32 fault in foreground verification");
            }
            return 0x1234;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeFalse("必须在异常发生时补偿释放 LCtrl");

        _fakeNative.RecordedInputs.Count.ShouldBe(2);
        ((ushort)_fakeNative.RecordedInputs[0].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[0].Input.Anonymous.ki.dwFlags.ShouldBe((KEYBD_EVENT_FLAGS)0);
        ((ushort)_fakeNative.RecordedInputs[1].Input.Anonymous.ki.wVk).ShouldBe((ushort)0xA2);
        _fakeNative.RecordedInputs[1].Input.Anonymous.ki.dwFlags.ShouldBe(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
    }

    [Fact]
    public async Task Concurrent_RetryCleanup_SharesActiveCleanupTask_NoDuplicateKeyUp()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        _fakeNative.CustomSendInputHandler = (c, inputs, sz) =>
        {
            var vk = (ushort)inputs[0].Anonymous.ki.wVk;
            var flags = inputs[0].Anonymous.ki.dwFlags;
            if (vk == 0x43 && flags == 0) return 0; // C down 失败
            if (vk == 0xA2 && flags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) return 0; // 初次补偿 LCtrl up 失败
            return 1;
        };

        var result = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(KeyboardCopyOutcome.Failed);
        simulator.HasPendingRelease.ShouldBeTrue();

        _fakeNative.RecordedInputs.Clear();
        _fakeNative.CustomSendInputHandler = null; // 后续发送允许成功
        _fakeNative.SendInputEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeNative.SendInputReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task? retryTask1 = null;
        Task? retryTask2 = null;
        try
        {
            retryTask1 = simulator.RetryCleanupAsync();

            await _fakeNative.SendInputEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            retryTask2 = simulator.RetryCleanupAsync();
            retryTask2.ShouldBe(retryTask1, "并发调用 RetryCleanup 必须共享登记的同一个清理 Task");

            var copyDuringCleanup = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
            copyDuringCleanup.Outcome.ShouldBe(KeyboardCopyOutcome.Busy, "清理期间严禁接纳 Copy");

            _fakeNative.SendInputReleaseTcs.TrySetResult();

            await retryTask1;
            await retryTask2;

            simulator.HasPendingRelease.ShouldBeFalse();

            var upInputs = _fakeNative.RecordedInputs.FindAll(r => (ushort)r.Input.Anonymous.ki.wVk == 0xA2 && r.Input.Anonymous.ki.dwFlags == KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
            upInputs.Count.ShouldBe(1, "并发清理不得遍历产生重复的 KeyUp 事件");
        }
        finally
        {
            _fakeNative.SendInputReleaseTcs?.TrySetResult();
            if (retryTask1 != null)
            {
                try { await retryTask1; } catch { }
            }
            if (retryTask2 != null)
            {
                try { await retryTask2; } catch { }
            }
        }
    }

    [Fact]
    public async Task CopyAsync_CompletedTask_EncompassesOwnFinallyBlock()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        var res1 = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        res1.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);

        var res2 = await simulator.CopyAsync(target, TestContext.Current.CancellationToken);
        res2.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
    }

    [Fact]
    public async Task Cancel_DuringFinalTargetVerification_DoesNotSendAnyInput_CompletesCleanly()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);
        using var cts = new CancellationTokenSource();

        int pidReads = 0;
        var enteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _fakeNative.CustomGetWindowProcessIdHandler = _ =>
        {
            if (++pidReads == 4)
            {
                enteredTcs.TrySetResult();
                releaseTcs.Task.GetAwaiter().GetResult();
            }
            return 5000;
        };

        Task<KeyboardCopyResult>? copyTask = null;
        try
        {
            copyTask = simulator.CopyAsync(target, cts.Token);

            await enteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            _fakeNative.RecordedInputs.Count.ShouldBe(0, "取消触发前尚未发送任何按键");
            cts.Cancel();
            releaseTcs.TrySetResult();

            await Should.ThrowAsync<OperationCanceledException>(() => copyTask);

            _fakeNative.RecordedInputs.Count.ShouldBe(0, "最终目标检查期间取消后绝对不能调用 SendInput");
            simulator.HasPendingRelease.ShouldBeFalse();
        }
        finally
        {
            releaseTcs.TrySetResult();
            if (copyTask != null)
            {
                try { await copyTask; } catch { }
            }
        }
    }

    [Fact]
    public async Task StopAsync_DuringFinalTargetVerification_DoesNotSendAnyInput_CompletesCleanly()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);

        int pidReads = 0;
        var enteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _fakeNative.CustomGetWindowProcessIdHandler = _ =>
        {
            if (++pidReads == 4)
            {
                enteredTcs.TrySetResult();
                releaseTcs.Task.GetAwaiter().GetResult();
            }
            return 5000;
        };

        Task<KeyboardCopyResult>? copyTask = null;
        Task? stopTask = null;
        try
        {
            copyTask = simulator.CopyAsync(target, TestContext.Current.CancellationToken);

            await enteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            _fakeNative.RecordedInputs.Count.ShouldBe(0, "StopAsync 触发前尚未发送任何按键");
            stopTask = simulator.StopAsync();
            releaseTcs.TrySetResult();

            await Should.ThrowAsync<OperationCanceledException>(() => copyTask);
            await stopTask;

            _fakeNative.RecordedInputs.Count.ShouldBe(0, "最终目标检查期间停止后绝对不能调用 SendInput");
            simulator.HasPendingRelease.ShouldBeFalse();
        }
        finally
        {
            releaseTcs.TrySetResult();
            if (copyTask != null)
            {
                try { await copyTask; } catch { }
            }
            if (stopTask != null)
            {
                try { await stopTask; } catch { }
            }
        }
    }

    [Fact]
    public async Task Cancel_AfterFirstSendInputEntered_ObservesReturnAndReleasesKeys_DoesNotThrowUnsent()
    {
        var simulator = CreateSimulator();
        var target = new KeyboardCopyTarget(0x1234, 5000);
        using var cts = new CancellationTokenSource();

        _fakeNative.SendInputEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fakeNative.SendInputReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<KeyboardCopyResult>? copyTask = null;
        try
        {
            copyTask = simulator.CopyAsync(target, cts.Token);

            await _fakeNative.SendInputEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            cts.Cancel();
            _fakeNative.SendInputReleaseTcs.TrySetResult();

            var result = await copyTask;
            result.Outcome.ShouldBe(KeyboardCopyOutcome.Injected);
            simulator.HasPendingRelease.ShouldBeFalse("首个 SendInput 已进入后取消，必须完整释放已确认按键，不得遗失释放责任");
            _fakeNative.RecordedInputs.Count.ShouldBe(4);
        }
        finally
        {
            _fakeNative.SendInputReleaseTcs?.TrySetResult();
            if (copyTask != null)
            {
                try { await copyTask; } catch { }
            }
        }
    }

    #endregion
}
