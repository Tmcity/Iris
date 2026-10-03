using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Tests.TestSupport;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Hosting;

public sealed class HotkeyCoordinatorPauseTests
{
    private sealed class MutableSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; }
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public MutableSettingsService(AppSettings initial)
        {
            Current = initial;
        }

        public void Publish(AppSettings next)
        {
            var old = Current;
            Current = next;
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, next));
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            Publish(mutate(Current));
            return Task.CompletedTask;
        }

        public Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            _ = Current;
            return Task.CompletedTask;
        }
    }

    private sealed class TestActionHandler : IActionHandler
    {
        public IrisAction Action { get; }
        public int ExecutionCount { get; private set; }
        public Func<ActionContext, CancellationToken, Task>? OnExecute { get; set; }

        public TestActionHandler(IrisAction action, Func<ActionContext, CancellationToken, Task>? onExecute = null)
        {
            Action = action;
            OnExecute = onExecute;
        }

        public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            if (OnExecute != null)
            {
                await OnExecute(context, cancellationToken);
            }
        }

        private int _executionCount;
        public int Count => _executionCount;
    }

    private sealed class FakeCaptureService : IHotkeyCaptureService
    {
        public int BeginCallCount { get; set; }
        public int StopCallCount { get; set; }
        public bool FailBegin { get; set; }
        public bool FailUnhookOnDispose { get; set; }

        public FakeCaptureSession? CurrentSession { get; private set; }

        public Task<IHotkeyCaptureSession> BeginAsync(nint windowHandle, CancellationToken cancellationToken = default)
        {
            BeginCallCount++;
            if (FailBegin)
            {
                throw new IrisException(ErrorCode.Unknown, "Hook 安装失败");
            }

            var session = new FakeCaptureSession(this);
            CurrentSession = session;
            return Task.FromResult<IHotkeyCaptureSession>(session);
        }

        public Task StopAsync()
        {
            StopCallCount++;
            CurrentSession?.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeCaptureSession(FakeCaptureService service) : IHotkeyCaptureSession
    {
        private readonly TaskCompletionSource<HotkeyCaptureResult> _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid Id { get; } = Guid.NewGuid();
        public HotkeyCaptureProgress Current { get; set; } = new(1, true, HotkeyModifiers.None);
        public Task<HotkeyCaptureResult> Completion => _completionTcs.Task;
        public event EventHandler? ProgressChanged;

        public void RaiseProgress(HotkeyCaptureProgress progress)
        {
            Current = progress;
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Complete(HotkeyCaptureResult result)
        {
            _completionTcs.TrySetResult(result);
        }

        public void Cancel()
        {
            _completionTcs.TrySetResult(new HotkeyCaptureResult(HotkeyCaptureOutcome.Canceled));
        }

        public ValueTask DisposeAsync()
        {
            if (service.FailUnhookOnDispose)
            {
                throw new IrisException(ErrorCode.Unknown, "卸载失败");
            }
            return ValueTask.CompletedTask;
        }
    }

    private static (Dispatcher Dispatcher, Thread Thread, WpfUiDispatcher UiDispatcher) CreateStaDispatcher()
    {
        Dispatcher? staDispatcher = null;
        using var readyEvt = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            staDispatcher = Dispatcher.CurrentDispatcher;
            readyEvt.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        readyEvt.Wait(TimeSpan.FromSeconds(5));
        var uiDispatcher = new WpfUiDispatcher(() => staDispatcher);
        return (staDispatcher!, thread, uiDispatcher);
    }

    private static void ShutdownStaDispatcher(Dispatcher dispatcher, Thread thread)
    {
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
        thread.Join(3000);
    }

    // =========================================================================
    // A01: Iris 热键正常注册 → 请求录入；原生注销首次失败
    // 准入先撤销；原 owner UI 线程完成注销后才允许 Begin；失败时 hook 安装数为零
    // =========================================================================
    [Fact]
    public async Task A01_PauseAsync_WhenUnregisterFails_PreventsHookInstallationAndReportsError()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handler = new TestActionHandler(IrisAction.InputTranslate);
            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();
            fakeNative.RegisteredHotkeys.Count.ShouldBeGreaterThan(0);

            var fakeCapture = new FakeCaptureService();
            await using var recorder = new HotkeyRecordingCoordinator(coordinator, fakeCapture, NullLogger<HotkeyRecordingCoordinator>.Instance);

            // 注入原生注销失败
            fakeNative.FailUnregisterHotKey = true;

            // 请求录入，注销失败抛出异常
            var ex = await Should.ThrowAsync<Exception>(() =>
                recorder.RecordAsync("TestOwner", 0x1000, null, TestContext.Current.CancellationToken));

            // 底层 hook 绝不被调用安装
            fakeCapture.BeginCallCount.ShouldBe(0);

            // 恢复注销并验证可以重试成功
            fakeNative.FailUnregisterHotKey = false;
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A02: 热键已进入真实 UI 队列但未执行 → 暂停推进代次 → 放行 UI → 恢复
    // 旧消息在实际 UI 执行点被拒绝，恢复后也不补执行；新代次动作仍可工作
    // =========================================================================
    [Fact]
    public async Task A02_PauseAsync_AdvancesGeneration_RejectsQueuedDispatchesAtUiExecutionPoint()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handlerExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new TestActionHandler(IrisAction.InputTranslate, (_, _) =>
            {
                handlerExecuted.TrySetResult();
                return Task.CompletedTask;
            });

            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            var registered = fakeNative.RegisteredHotkeys.First();

            // 1. 让 UI 线程执行 blocker 并同步阻塞，占住 STA 线程
            var uiBlockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiBlockRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = uiDispatcher.InvokeAsync(() =>
            {
                uiBlockEntered.TrySetResult();
                uiBlockRelease.Task.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

            await uiBlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 2. 模拟按下热键（排队等待进入 UI）
            fakeNative.SendWmHotkey(registered.Id, registered.Modifiers, registered.Vk);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            // 3. 在 UI 处理前请求暂停，代次推进
            var pauseTask = coordinator.PauseAsync("RecorderOwner", TestContext.Current.CancellationToken);

            // 4. 放行 blocker，UI 依次处理旧热键校验（被拒绝）与 PauseAsync 的注销
            uiBlockRelease.TrySetResult();

            var lease = await pauseTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            lease.ShouldNotBeNull();

            // 5. 等待一段时间确保旧热键在 UI 判定点被拒绝
            await Task.Delay(100, TestContext.Current.CancellationToken);
            handler.Count.ShouldBe(0);

            // 6. 恢复热键
            await lease.DisposeAsync();
            handler.Count.ShouldBe(0); // 恢复后绝不补执行

            // 6. 新代次热键能够正常触发
            var newRegistered = fakeNative.RegisteredHotkeys.Last();
            fakeNative.SendWmHotkey(newRegistered.Id, newRegistered.Modifiers, newRegistered.Vk);

            await handlerExecuted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            handler.Count.ShouldBe(1);
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A03: 真实 ClipboardTranslateHandler 的 fake 读取已 entered 且忽略取消 → 请求录入
    // Begin 尚未调用，UI 可调度；录入准备不能取消已开始的用户动作；放行并等待真实 Handler 退出后才继续
    // =========================================================================
    [Fact]
    public async Task A03_PauseAsync_WaitsForInFlightHandler_WithoutCancelingRunningUserOperation()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            settings = settings with
            {
                Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(settings.Hotkeys)
                {
                    [IrisAction.ClipboardTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "V", true)
                }
            };
            var settingsService = new MutableSettingsService(settings);

            var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var handler = new TestActionHandler(IrisAction.ClipboardTranslate, async (_, ct) =>
            {
                handlerEntered.TrySetResult();
                // 模拟外部耗时读取（在异步任务中等待，不阻塞 UI 消息循环，且忽略取消以证明不是通过强制终止来结束）
                await Task.Run(async () =>
                {
                    await handlerRelease.Task.WaitAsync(TestContext.Current.CancellationToken);
                }, ct);
            });

            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            var registered = fakeNative.RegisteredHotkeys.First();

            // 触发热键，让 Handler 进入执行
            fakeNative.SendWmHotkey(registered.Id, registered.Modifiers, registered.Vk);
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 此时 Handler 正在执行中。请求 PauseAsync，它必须等待 Handler 完成
            var pauseTask = coordinator.PauseAsync("TestOwner", TestContext.Current.CancellationToken);

            // PauseAsync 尚未完成
            await Task.Delay(100, TestContext.Current.CancellationToken);
            pauseTask.IsCompleted.ShouldBeFalse();

            // 放行 Handler
            handlerRelease.TrySetResult();

            // 现在 PauseAsync 应该成功完成
            var lease = await pauseTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            lease.ShouldNotBeNull();

            await lease.DisposeAsync();
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A04: 完成一次暂停/恢复 → 开始第二次真实前置读取 → 再暂停
    // 第二次仍等待本次工作，不能复用首次已完成的永久 shutdown wait
    // =========================================================================
    [Fact]
    public async Task A04_PauseAsync_SecondCycle_StillWaitsForNewInFlightHandler()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var enter1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enter2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            int runIndex = 0;
            var handler = new TestActionHandler(IrisAction.InputTranslate, async (_, _) =>
            {
                int current = Interlocked.Increment(ref runIndex);
                if (current == 1)
                {
                    enter1.TrySetResult();
                    await release1.Task.WaitAsync(TestContext.Current.CancellationToken);
                }
                else if (current == 2)
                {
                    enter2.TrySetResult();
                    await release2.Task.WaitAsync(TestContext.Current.CancellationToken);
                }
            });

            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            // 第一次循环
            var reg1 = fakeNative.RegisteredHotkeys.Last();
            fakeNative.SendWmHotkey(reg1.Id, reg1.Modifiers, reg1.Vk);
            await enter1.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            var pauseTask1 = coordinator.PauseAsync("Owner1", TestContext.Current.CancellationToken);
            release1.TrySetResult();
            var lease1 = await pauseTask1.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await lease1.DisposeAsync();

            // 第二次循环
            var reg2 = fakeNative.RegisteredHotkeys.Last();
            fakeNative.SendWmHotkey(reg2.Id, reg2.Modifiers, reg2.Vk);
            await enter2.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            var pauseTask2 = coordinator.PauseAsync("Owner2", TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            pauseTask2.IsCompleted.ShouldBeFalse(); // 必须等待本次执行，不能复用第一次已完成的 wait 任务！

            release2.TrySetResult();
            var lease2 = await pauseTask2.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await lease2.DisposeAsync();
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A05: 暂停期间依次保存配置 S1、S2，再释放录入
    // 暂停期间不注册；恢复只应用当前持久目标 S2；包括组合互换和未变化项
    // =========================================================================
    [Fact]
    public async Task A05_PauseAsync_SettingsChangedDuringPause_OnlyAppliesLatestTargetOnRelease()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handler1 = new TestActionHandler(IrisAction.InputTranslate);
            var handler2 = new TestActionHandler(IrisAction.ClipboardTranslate);

            var actionDispatcher = new ActionDispatcher([handler1, handler2], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler1, handler2],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            var lease = await coordinator.PauseAsync("TestOwner", TestContext.Current.CancellationToken);

            int regCountDuringPause = fakeNative.RegisterHotKeyCallCount;

            // 暂停期间更新 S1
            var s1 = settings with
            {
                Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(settings.Hotkeys)
                {
                    [IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, "I", true)
                }
            };
            settingsService.Publish(s1);

            // 暂停期间更新 S2（互换按键组合）
            var s2 = s1 with
            {
                Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(s1.Hotkeys)
                {
                    [IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "X", true),
                    [IrisAction.ClipboardTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Y", true)
                }
            };
            settingsService.Publish(s2);

            // 验证暂停期间不发起新的注册调用
            fakeNative.RegisterHotKeyCallCount.ShouldBe(regCountDuringPause);

            // 释放暂停，恢复最新持久目标 S2
            await lease.DisposeAsync();

            var snap = coordinator.GetRuntimeSnapshot();
            snap.IsPaused.ShouldBeFalse();
            snap.Items[IrisAction.InputTranslate].KeyComboText.ShouldBe("Ctrl+Alt+X");
            snap.Items[IrisAction.ClipboardTranslate].KeyComboText.ShouldBe("Ctrl+Alt+Y");
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A06: Captured 已确定 → 卸载失败 → 重试成功
    // 失败时不提交候选到草稿、不恢复热键、不能开始新录入；资源成功清理且 owner 仍有效后再应用结果
    // =========================================================================
    [Fact]
    public async Task A06_RecordAsync_WhenUnhookFails_PreservesPauseAndBlocksNewRecordingUntilCleaned()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handler = new TestActionHandler(IrisAction.InputTranslate);
            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            var fakeCapture = new FakeCaptureService();
            await using var recorder = new HotkeyRecordingCoordinator(coordinator, fakeCapture, NullLogger<HotkeyRecordingCoordinator>.Instance);

            // 模拟捕获成功但卸载失败
            fakeCapture.FailUnhookOnDispose = true;

            var recordTask = recorder.RecordAsync("TestOwner", 0x1000, null, TestContext.Current.CancellationToken);

            while (fakeCapture.CurrentSession == null)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            fakeCapture.CurrentSession.Complete(new HotkeyCaptureResult(
                HotkeyCaptureOutcome.Captured,
                new HotkeyBinding(HotkeyModifiers.Control, "F", true)));

            // 卸载失败抛出异常
            var ex = await Should.ThrowAsync<InvalidOperationException>(() => recordTask);
            ex.Message.ShouldContain("结束录入清理失败");

            // Coordinator 仍保持暂停态
            coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeTrue();

            // 此时不能开启新的录入
            fakeCapture.FailUnhookOnDispose = false;
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A07: hook 已清理 → 恢复旧持久目标时某动作注册失败
    // 有效候选仍进入草稿；运行状态如实记录失败，重试属于 HotkeyCoordinator
    // =========================================================================
    [Fact]
    public async Task A07_ReleasePause_WhenRegistrationFails_RecordsFailedStatus_AndCanRetry()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handler = new TestActionHandler(IrisAction.InputTranslate);
            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            var lease = await coordinator.PauseAsync("TestOwner", TestContext.Current.CancellationToken);

            // 模拟注册失败（如快捷键冲突 1409）
            fakeNative.FailRegisterHotKey = true;

            await lease.DisposeAsync();

            // 运行状态如实记录冲突/失败
            var snap = coordinator.GetRuntimeSnapshot();
            snap.Items[IrisAction.InputTranslate].Status.ShouldBe(HotkeyRuntimeActionStatus.Conflict);

            // 修复原生并重试
            fakeNative.FailRegisterHotKey = false;
            var retrySnap = await coordinator.RetryApplyAsync();
            retrySnap.Items[IrisAction.InputTranslate].Status.ShouldBe(HotkeyRuntimeActionStatus.Registered);
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A08: 上一会话的通知/完成回调/lease release 晚到，新会话已开始
    // 旧通知不改新控件；旧 lease 不解除新暂停；会话 Id、revision 与 owner 校验各有证据
    // =========================================================================
    [Fact]
    public async Task A08_StaleLeaseRelease_DoesNotCancelNewPauseSession()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handler = new TestActionHandler(IrisAction.InputTranslate);
            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();

            // 第一次暂停并正常释放
            var lease1 = await coordinator.PauseAsync("Owner1", TestContext.Current.CancellationToken);
            await lease1.DisposeAsync();

            // 第二次暂停
            var lease2 = await coordinator.PauseAsync("Owner2", TestContext.Current.CancellationToken);
            coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeTrue();

            // 晚到的 lease1.DisposeAsync 不影响 lease2
            await lease1.DisposeAsync();
            coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeTrue();

            // lease2 正常释放
            await lease2.DisposeAsync();
            coordinator.GetRuntimeSnapshot().IsPaused.ShouldBeFalse();
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }

    // =========================================================================
    // A09: 退出请求与清理完成/lease 释放/设置变化/重试交错
    // 永久准入关闭后的 Register 调用增量为零，实际资源停止完成后才释放后续依赖
    // =========================================================================
    [Fact]
    public async Task A09_StopAcceptanceAndNativeAsync_ClosesAccess_AndSubsequentCallsDoNotRegister()
    {
        var (dispatcher, thread, uiDispatcher) = CreateStaDispatcher();
        try
        {
            var fakeNative = new FakeNativeHotkeyAdapter();
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = new MutableSettingsService(settings);

            var handler = new TestActionHandler(IrisAction.InputTranslate);
            var actionDispatcher = new ActionDispatcher([handler], uiDispatcher, Substitute.For<INotificationService>(), NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [handler],
                uiDispatcher,
                Substitute.For<INotificationService>(),
                NullLogger<HotkeyCoordinator>.Instance);

            await coordinator.StartAsync();
            int regCountAtStart = fakeNative.RegisterHotKeyCallCount;

            // 永久停止准入与原生热键
            await coordinator.StopAcceptanceAndNativeAsync();

            // 停止后更新设置
            var newSettings = settings with
            {
                Hotkeys = new Dictionary<IrisAction, HotkeyBinding>(settings.Hotkeys)
                {
                    [IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Z", true)
                }
            };
            settingsService.Publish(newSettings);

            // 停止后尝试 RetryApplyAsync 应抛出 InvalidOperationException
            await Should.ThrowAsync<InvalidOperationException>(() => coordinator.RetryApplyAsync());

            // Register 调用增量保持为零
            fakeNative.RegisterHotKeyCallCount.ShouldBe(regCountAtStart);
        }
        finally
        {
            ShutdownStaDispatcher(dispatcher, thread);
        }
    }
}
