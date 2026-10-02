using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
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

using Iris.App.Tests.TestSupport;
using Iris.Core.Workflows;

namespace Iris.App.Tests.Hosting;

public sealed class HotkeyCoordinatorTests
{
    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool AccessGranted { get; set; } = true;
        public int InvokeCallCount { get; private set; }
        public Func<Func<Task>, Task>? CustomInvoke { get; set; }

        public bool CheckAccess() => AccessGranted;

        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            InvokeCallCount++;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (CustomInvoke != null)
            {
                await CustomInvoke(callback);
                return;
            }
            await callback();
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<(NotificationLevel Level, string Title, string Message)> Notifications { get; } = [];

        public void Notify(NotificationLevel level, string title, string message)
        {
            Notifications.Add((level, title, message));
        }
    }

    private sealed class TestActionHandler(IrisAction action, Func<ActionContext, Task>? onExecute = null) : IActionHandler
    {
        public IrisAction Action { get; } = action;
        public int ExecutionCount { get; private set; }

        public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            if (onExecute != null)
            {
                await onExecute(context);
            }
        }
    }

    [Fact]
    public async Task H09_DefaultSettings_OnlyDeliveredAndEnabledActionsAreRegistered()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings(); // Uses HotkeyDefaults.Create()
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        // Delivered handlers: InputTranslate, ClipboardTranslate, TranslateText, OpenSettings
        var handlers = new IActionHandler[]
        {
            new TestActionHandler(IrisAction.InputTranslate),
            new TestActionHandler(IrisAction.ClipboardTranslate),
            new TestActionHandler(IrisAction.TranslateText),
            new TestActionHandler(IrisAction.OpenSettings)
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        // In HotkeyDefaults:
        // InputTranslate: Control | Alt, "F", Enabled: true -> Registered!
        // ClipboardTranslate: Control | Alt, "V", Enabled: false -> NOT registered!
        // SelectionTranslate, ScreenshotTranslate, etc. have Enabled: true in defaults, but NO handler delivered -> NOT registered!
        fakeNative.RegisteredHotkeys.Count.ShouldBe(1);
        var reg = fakeNative.RegisteredHotkeys[0];
        reg.Vk.ShouldBe(0x46u); // 'F'
        (reg.Modifiers & 0x4000).ShouldBe(0x4000u); // MOD_NOREPEAT
        (reg.Modifiers & 0x1).ShouldBe(0x1u); // Alt
        (reg.Modifiers & 0x2).ShouldBe(0x2u); // Control
    }

    [Fact]
    public async Task H10_IrrelevantSettingsUpdates_DoNotReRegisterHotkeys()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var oldSettings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(oldSettings);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();
        fakeNative.RegisterHotKeyCallCount.ShouldBe(1);

        // 1. Change Theme and LogLevel only
        var newSettings1 = oldSettings with
        {
            General = oldSettings.General with { Theme = AppTheme.Dark, LogLevel = "Debug" }
        };


        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService, new SettingsChangedEventArgs(oldSettings, newSettings1));

        // No new native register calls
        fakeNative.RegisterHotKeyCallCount.ShouldBe(1);
        fakeNative.UnregisterHotKeyCallCount.ShouldBe(0);

        // 2. Deep-copied Hotkeys dictionary with identical semantic values
        var clonedHotkeys = new Dictionary<IrisAction, HotkeyBinding>(oldSettings.Hotkeys);
        var newSettings2 = oldSettings with { Hotkeys = clonedHotkeys };

        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService, new SettingsChangedEventArgs(oldSettings, newSettings2));

        fakeNative.RegisterHotKeyCallCount.ShouldBe(1);
        fakeNative.UnregisterHotKeyCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task H11_KeyCombinationSwap_UnregistersFirst_AndSucceedsInStableOrder()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        // Initial setup:
        // InputTranslate = Ctrl+Alt+1 (D1)
        // ClipboardTranslate = Ctrl+Alt+2 (D2), Enabled=true
        var initialHotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D1", Enabled: true),
            [IrisAction.ClipboardTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D2", Enabled: true)
        };
        var settings1 = new AppSettings { Hotkeys = initialHotkeys };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings1);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[]
        {
            new TestActionHandler(IrisAction.InputTranslate),
            new TestActionHandler(IrisAction.ClipboardTranslate)
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();
        fakeNative.RegisteredHotkeys.Count.ShouldBe(2);

        // Swap combinations:
        // InputTranslate = Ctrl+Alt+2 (D2)
        // ClipboardTranslate = Ctrl+Alt+1 (D1)
        var swappedHotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D2", Enabled: true),
            [IrisAction.ClipboardTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D1", Enabled: true)
        };
        var settings2 = new AppSettings { Hotkeys = swappedHotkeys };

        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService, new SettingsChangedEventArgs(settings1, settings2));

        // Both old keys were unregistered first (count = 2)
        fakeNative.UnregisteredHotkeys.Count.ShouldBe(2);

        // Both new keys were registered (count = 4 total registered)
        fakeNative.RegisteredHotkeys.Count.ShouldBe(4);

        // Verify the newly registered entries
        var newlyRegistered = fakeNative.RegisteredHotkeys.Skip(2).ToList();
        newlyRegistered.Count.ShouldBe(2);

        // Action values: InputTranslate = 1, ClipboardTranslate = 6
        // Ordered by IrisAction enum value: InputTranslate (1) registered before ClipboardTranslate (6)
        newlyRegistered[0].Vk.ShouldBe(0x32u); // InputTranslate received D2
        newlyRegistered[1].Vk.ShouldBe(0x31u); // ClipboardTranslate received D1
    }

    [Fact]
    public async Task H12_RegistrationFailure_NotifiesUserWithActionAndKeyCombo()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F", Enabled: true)
        };
        var settings = new AppSettings { Hotkeys = hotkeys };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        // Simulate conflict on native registration
        fakeNative.FailRegisterHotKey = true;
        fakeNative.LastError = 1409;

        await coordinator.StartAsync();

        // Notification should have been delivered
        notificationService.Notifications.Count.ShouldBe(1);
        var notif = notificationService.Notifications[0];
        notif.Level.ShouldBe(NotificationLevel.Warning);
        notif.Message.ShouldContain("输入翻译");
        notif.Message.ShouldContain("Ctrl+Alt+F");
        notif.Message.ShouldContain(ErrorMessages.GetDefault(ErrorCode.HotkeyConflict));
    }

    [Fact]
    public async Task H12_UnregisterFailure_NotifiesUser_AndRetriesOnNextSettingsUpdate()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var initialHotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "A", Enabled: true)
        };
        var settings1 = new AppSettings { Hotkeys = initialHotkeys };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings1);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();
        fakeNative.RegisteredHotkeys.Count.ShouldBe(1);

        // 1. Simulate unregister failure on settings change
        fakeNative.FailUnregisterHotKey = true;
        var newHotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "B", Enabled: true)
        };
        var settings2 = new AppSettings { Hotkeys = newHotkeys };

        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService, new SettingsChangedEventArgs(settings1, settings2));

        // Unregister failed -> user notified, new binding NOT registered!
        notificationService.Notifications.Count.ShouldBe(1);
        notificationService.Notifications[0].Message.ShouldContain("输入翻译");
        fakeNative.RegisterHotKeyCallCount.ShouldBe(1); // Still only initial register

        // 2. Clear failure and update settings again -> retries unregister and succeeds
        fakeNative.FailUnregisterHotKey = false;
        var settings3 = new AppSettings { Hotkeys = newHotkeys };

        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService, new SettingsChangedEventArgs(settings1, settings3));

        // Unregister retried and succeeded, new key registered!
        fakeNative.UnregisterHotKeyCallCount.ShouldBe(2); // 1 failed, 1 succeeded retry
        fakeNative.RegisterHotKeyCallCount.ShouldBe(2); // new binding registered
    }

    [Fact]
    public async Task H12_FormatKeyCombo_SanitizesInvalidKeyNames()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        // Raw invalid key string that could leak arbitrary text
        string rawSecretOrScript = "<script>alert(1)</script>_SECRET_123";
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>
        {
            [IrisAction.InputTranslate] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, rawSecretOrScript, Enabled: true)
        };
        var settings = new AppSettings { Hotkeys = hotkeys };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        // Notification must NOT contain raw string, but must contain sanitized placeholder
        notificationService.Notifications.Count.ShouldBe(1);
        var msg = notificationService.Notifications[0].Message;
        msg.ShouldNotContain(rawSecretOrScript);
        msg.ShouldContain("Ctrl+Alt+(无效键)");
    }

    [Fact]
    public async Task H13_Pressed_RoutesToActionDispatcher_WithHotkeyTrigger_AndBoundedQueue()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var dispatchedContexts = new List<ActionContext>();
        var firstDispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                dispatchedContexts.Add(callInfo.Arg<ActionContext>());
                firstDispatchStarted.TrySetResult();
                await tcs.Task; // Hold dispatch
            });

        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        int id = fakeNative.RegisteredHotkeys[0].Id;

        try
        {
            // Trigger WM_HOTKEY via fake native adapter for ID (InputTranslate)
            fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);

            // Wait until DispatchAsync actually starts on thread pool
            await firstDispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            dispatchedContexts.Count.ShouldBe(1);
            dispatchedContexts[0].Trigger.ShouldBe(ActionTrigger.Hotkey);

            // Trigger again while first is still running -> dropped by bounded queue
            fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
            dispatchedContexts.Count.ShouldBe(1);

            // Release first dispatch
            tcs.TrySetResult();
            await coordinator.WaitForInFlightDispatchesAsync();

            // Now next hotkey message can be accepted
            var secondDispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    dispatchedContexts.Add(callInfo.Arg<ActionContext>());
                    secondDispatchStarted.TrySetResult();
                    return Task.CompletedTask;
                });

            fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
            await secondDispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            dispatchedContexts.Count.ShouldBe(2);
        }
        finally
        {
            tcs.TrySetResult();
            await coordinator.WaitForInFlightDispatchesAsync();
        }
    }

    [Fact]
    public async Task H13_OnHotkeyPressed_DoesNotBlockWndProc_RunsOnThreadPool()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        int callerThreadId = Environment.CurrentManagedThreadId;
        int? dispatchThreadId = null;
        bool insideWndProc = false;
        bool? handlerStartedInsideWndProc = null;
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                handlerStartedInsideWndProc = insideWndProc;
                dispatchThreadId = Environment.CurrentManagedThreadId;
                dispatchStarted.TrySetResult();
                await dispatchFinish.Task;
            });

        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        int id = fakeNative.RegisteredHotkeys[0].Id;

        try
        {
            // Send hotkey: SendWmHotkey must return immediately even though dispatch is held
            insideWndProc = true;
            try
            {
                fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
            }
            finally
            {
                insideWndProc = false;
            }

            // WndProc did NOT block and has already returned before handler was started!
            await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            handlerStartedInsideWndProc.ShouldBe(false, "分发绝不得在 WndProc 栈内同步开始");
            dispatchThreadId.ShouldNotBeNull();
            dispatchThreadId.Value.ShouldNotBe(callerThreadId, "分发绝不得在 WndProc 发送线程上同步执行");
        }
        finally
        {
            dispatchFinish.TrySetResult();
            await coordinator.WaitForInFlightDispatchesAsync();
        }
    }

    [Fact]
    public async Task H14_StopAcceptanceAndNativeAsync_DisposesNative_AndRejectsQueuedMessages()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();
        fakeNative.RegisteredHotkeys.Count.ShouldBe(1);
        int id = fakeNative.RegisteredHotkeys[0].Id;

        // Stop coordinator
        await coordinator.StopAcceptanceAndNativeAsync();

        // Native resources disposed
        fakeNative.UnregisteredHotkeys.Count.ShouldBe(1);

        // Post-stop message is rejected
        fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
        await actionDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task H15_InFlightDispatches_ObservedDuringShutdown()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var dispatchStarted = new TaskCompletionSource();
        var tcs = new TaskCompletionSource();
        var actionDispatcher = Substitute.For<IActionDispatcher>();
        actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                dispatchStarted.TrySetResult();
                await tcs.Task;
            });

        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        int id = fakeNative.RegisteredHotkeys[0].Id;
        // Start a dispatch
        fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
        await dispatchStarted.Task;

        try
        {
            // Stage 1: Stop acceptance and native resources
            await coordinator.StopAcceptanceAndNativeAsync();

            // Stage 2: Wait for in-flight dispatches
            var waitTask = coordinator.WaitForInFlightDispatchesAsync();
            waitTask.IsCompleted.ShouldBeFalse();

            // Complete the in-flight dispatch
            tcs.TrySetResult();
            await waitTask;
            waitTask.IsCompletedSuccessfully.ShouldBeTrue();
        }
        finally
        {
            tcs.TrySetResult();
            await coordinator.WaitForInFlightDispatchesAsync();
        }
    }

    [Fact]
    public async Task H16_StopAcceptanceAndNativeAsync_RetriesOnError_AndSharesBarrier()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        var uiDispatcher = new ImmediateUiDispatcher();
        var notificationService = new FakeNotificationService();

        var handlers = new IActionHandler[] { new TestActionHandler(IrisAction.InputTranslate) };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            actionDispatcher,
            handlers,
            uiDispatcher,
            notificationService,
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        // 1. Simulate failure during DestroyWindow in Dispose
        fakeNative.FailDestroyWindow = true;
        await Should.ThrowAsync<IrisException>(() => coordinator.StopAcceptanceAndNativeAsync());

        // 2. Retry succeeds once failure clears
        fakeNative.FailDestroyWindow = false;
        await Should.NotThrowAsync(() => coordinator.StopAcceptanceAndNativeAsync());

        // 3. Concurrent stops share the same barrier
        var t1 = coordinator.StopAcceptanceAndNativeAsync();
        var t2 = coordinator.StopAcceptanceAndNativeAsync();
        t1.ShouldBeSameAs(t2);
        await t1;
    }

    [Fact]
    public async Task SettingsQueue_BackgroundSubmitWhenConsumerAboutToIdle_DoesNotLoseUpdate()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var enteredFirstApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var uiDispatcher = new ImmediateUiDispatcher();
        int applyCount = 0;
        uiDispatcher.CustomInvoke = async callback =>
        {
            applyCount++;
            if (applyCount == 1)
            {
                await callback();
                return;
            }

            if (applyCount == 2)
            {
                enteredFirstApply.TrySetResult();
                await releaseFirstApply.Task;
            }

            await callback();
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            Substitute.For<IActionDispatcher>(),
            [new TestActionHandler(IrisAction.InputTranslate)],
            uiDispatcher,
            new FakeNotificationService(),
            NullLogger<HotkeyCoordinator>.Instance);

        try
        {
            await coordinator.StartAsync();
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x46u); // F

            var settingsB = CreateSettingsWithInputKey("B");
            var oldSettings = settings;
            settings = settingsB;
            settingsService.Current.Returns(settingsB);

            // Fire first Changed on background thread
            _ = Task.Run(() =>
            {
                settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                    settingsService, new SettingsChangedEventArgs(oldSettings, settingsB));
            }, TestContext.Current.CancellationToken);

            await enteredFirstApply.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // While consumer is processing S1, post S2 (key C)
            var settingsC = CreateSettingsWithInputKey("C");
            var oldB = settingsB;
            settings = settingsC;
            settingsService.Current.Returns(settingsC);

            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(oldB, settingsC));

            // Release first apply so consumer loop checks pendingSettings before releasing processing token
            releaseFirstApply.TrySetResult();

            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == 0x43u, TimeSpan.FromSeconds(3));
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x43u); // 'C' is registered without being lost
        }
        finally
        {
            releaseFirstApply.TrySetResult();
            await coordinator.StopAcceptanceAndNativeAsync();
        }
    }

    [Fact]
    public async Task SettingsQueue_MultipleRapidUpdates_OnlyLatestSnapshotTakesEffect()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var uiDispatcher = new ImmediateUiDispatcher();
        var holdInvoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool shouldHold = false;

        uiDispatcher.CustomInvoke = async callback =>
        {
            if (shouldHold)
            {
                await holdInvoke.Task;
            }
            await callback();
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            Substitute.For<IActionDispatcher>(),
            [new TestActionHandler(IrisAction.InputTranslate)],
            uiDispatcher,
            new FakeNotificationService(),
            NullLogger<HotkeyCoordinator>.Instance);

        try
        {
            await coordinator.StartAsync();

            shouldHold = true;

            AppSettings prev = settings;
            for (int i = 0; i < 8; i++)
            {
                char keyChar = (char)('A' + i);
                var next = CreateSettingsWithInputKey(keyChar.ToString());
                var capturedPrev = prev;
                prev = next;
                settingsService.Current.Returns(next);
                settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                    settingsService, new SettingsChangedEventArgs(capturedPrev, next));
            }

            // Release hold
            holdInvoke.TrySetResult();

            char expectedLastKey = (char)('A' + 7);
            uint expectedVk = (uint)expectedLastKey;
            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == expectedVk, TimeSpan.FromSeconds(3));

            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(expectedVk);
        }
        finally
        {
            holdInvoke.TrySetResult();
            await coordinator.StopAcceptanceAndNativeAsync();
        }
    }

    [Fact]
    public async Task SettingsQueue_DispatchFaultedOrCanceled_RecoversOnNextChange_WithoutInfiniteRetry()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var uiDispatcher = new ImmediateUiDispatcher();
        bool failNext = false;
        bool cancelNext = false;

        uiDispatcher.CustomInvoke = async callback =>
        {
            if (failNext)
            {
                failNext = false;
                throw new InvalidOperationException("Injected UI dispatcher failure");
            }
            if (cancelNext)
            {
                cancelNext = false;
                throw new OperationCanceledException("Injected UI dispatcher cancellation");
            }
            await callback();
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            Substitute.For<IActionDispatcher>(),
            [new TestActionHandler(IrisAction.InputTranslate)],
            uiDispatcher,
            new FakeNotificationService(),
            NullLogger<HotkeyCoordinator>.Instance);

        try
        {
            await coordinator.StartAsync();
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x46u); // Initial F

            // Step 1: Injected failure on change B
            failNext = true;
            var settingsB = CreateSettingsWithInputKey("B");
            settingsService.Current.Returns(settingsB);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settings, settingsB));

            await Task.Delay(50, TestContext.Current.CancellationToken);

            // B failed, so last registered is still F (not infinitely retrying B)
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x46u);

            // Step 2: Next valid change C arrives and recovers cleanly!
            var settingsC = CreateSettingsWithInputKey("C");
            settingsService.Current.Returns(settingsC);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settingsB, settingsC));

            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == 0x43u, TimeSpan.FromSeconds(3));
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x43u); // 'C' succeeded!

            // Step 3: Injected cancellation on change D
            cancelNext = true;
            var settingsD = CreateSettingsWithInputKey("D");
            settingsService.Current.Returns(settingsD);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settingsC, settingsD));

            await Task.Delay(50, TestContext.Current.CancellationToken);

            // Step 4: Next valid change E arrives and recovers from cancellation!
            var settingsE = CreateSettingsWithInputKey("E");
            settingsService.Current.Returns(settingsE);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settingsD, settingsE));

            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == 0x45u, TimeSpan.FromSeconds(3));
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x45u); // 'E' succeeded!
        }
        finally
        {
            await coordinator.StopAcceptanceAndNativeAsync();
        }
    }

    [Fact]
    public async Task SettingsQueue_AfterStopped_PendingSettingsAreNotProcessed()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var uiDispatcher = new ImmediateUiDispatcher();
        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            Substitute.For<IActionDispatcher>(),
            [new TestActionHandler(IrisAction.InputTranslate)],
            uiDispatcher,
            new FakeNotificationService(),
            NullLogger<HotkeyCoordinator>.Instance);

        await coordinator.StartAsync();

        await coordinator.StopAcceptanceAndNativeAsync();

        // Fire settings change after stop
        var newSettings = CreateSettingsWithInputKey("Z");
        settingsService.Current.Returns(newSettings);
        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService, new SettingsChangedEventArgs(settings, newSettings));

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // 'Z' is never registered
        fakeNative.RegisteredHotkeys.Any(r => r.Vk == 0x5Au).ShouldBeFalse();
    }

    private static AppSettings CreateSettingsWithInputKey(string key)
    {
        var hotkeys = HotkeyDefaults.Create().ToDictionary();
        hotkeys[IrisAction.InputTranslate] = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, key, true);
        return new AppSettings { Hotkeys = hotkeys };
    }

    private sealed class TrackingHandler(IActionHandler inner) : IActionHandler
    {
        public IrisAction Action => inner.Action;
        public ActionContext? CapturedContext { get; private set; }

        public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
        {
            CapturedContext = context;
            await inner.ExecuteAsync(context, cancellationToken);
        }
    }

    [Fact]
    public async Task RealIntegration_HotkeyTrigger_RunsOnOwnerUiThread_AfterLeavingWndProc()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var fakeNative = new FakeNativeHotkeyAdapter
            {
                CurrentThreadId = (uint)Environment.CurrentManagedThreadId
            };
            using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

            var settings = new AppSettings();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(settings);

            var uiDispatcher = new WpfUiDispatcher();
            var notificationService = new FakeNotificationService();

            var presenter = Substitute.For<ITranslateWindowPresenter>();
            var fakeOp = Substitute.For<ITranslateWindowOperation>();
            presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(fakeOp);

            bool? executedInsideWndProc = null;
            bool? executedOnUiThread = null;
            var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            bool insideWndProc = false;

            presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>())
                .Returns(async _ =>
                {
                    executedInsideWndProc = insideWndProc;
                    executedOnUiThread = uiDispatcher.CheckAccess();
                    handlerStarted.TrySetResult();
                    await handlerRelease.Task;
                    return true;
                });

            var deliveredHandler = new InputTranslateHandler(presenter);
            var trackingHandler = new TrackingHandler(deliveredHandler);

            var actionDispatcher = new ActionDispatcher(
                [trackingHandler],
                uiDispatcher,
                notificationService,
                NullLogger<ActionDispatcher>.Instance);

            using var coordinator = new HotkeyCoordinator(
                settingsService,
                hotkeyService,
                actionDispatcher,
                [trackingHandler],
                uiDispatcher,
                notificationService,
                NullLogger<HotkeyCoordinator>.Instance);

            try
            {
                await coordinator.StartAsync();

                fakeNative.RegisteredHotkeys.Count.ShouldBeGreaterThan(0);
                int id = fakeNative.RegisteredHotkeys[0].Id;

                insideWndProc = true;
                try
                {
                    fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
                }
                finally
                {
                    insideWndProc = false;
                }

                // Wait until the handler actually begins execution
                await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // Confirm: Trigger is Hotkey
                trackingHandler.CapturedContext.ShouldNotBeNull();
                trackingHandler.CapturedContext.Trigger.ShouldBe(ActionTrigger.Hotkey);

                // Confirm: Handler executed on owner UI thread (WPF Dispatcher)
                executedOnUiThread.ShouldBe(true);

                // Confirm: Handler started AFTER leaving WndProc call stack
                executedInsideWndProc.ShouldBe(false, "Handler 必须在离开 WndProc 栈后才开始执行");
            }
            finally
            {
                handlerRelease.TrySetResult();
                await coordinator.StopAcceptanceAndNativeAsync();
                await coordinator.WaitForInFlightDispatchesAsync();
            }
        });
    }

    [Fact]
    public async Task SettingsQueue_WhenFaultCallbackPending_NewChangedArrives_DispatchesNewSnapshotWithoutLosingConsumer()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var uiDispatcher = new ImmediateUiDispatcher();
        var faultTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool shouldFault = false;

        uiDispatcher.CustomInvoke = async callback =>
        {
            if (shouldFault)
            {
                shouldFault = false;
                await faultTcs.Task;
            }
            await callback();
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            Substitute.For<IActionDispatcher>(),
            [new TestActionHandler(IrisAction.InputTranslate)],
            uiDispatcher,
            new FakeNotificationService(),
            NullLogger<HotkeyCoordinator>.Instance);

        try
        {
            await coordinator.StartAsync();
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x46u); // Initial F

            // Prepare B to fault
            shouldFault = true;
            var settingsB = CreateSettingsWithInputKey("B");
            settingsService.Current.Returns(settingsB);

            // Trigger B: enters CustomInvoke and waits on faultTcs.Task
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settings, settingsB));

            // While B's dispatch is pending/blocked before failure, post C
            var settingsC = CreateSettingsWithInputKey("C");
            settingsService.Current.Returns(settingsC);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settingsB, settingsC));

            // Now fail B's task
            faultTcs.SetException(new InvalidOperationException("Injected dispatch failure for B"));

            // OnDispatchCompleted for B runs: detects new pending snapshot C arrived, so it schedules C!
            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == 0x43u, TimeSpan.FromSeconds(3));
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x43u); // 'C' succeeded and was not lost!
        }
        finally
        {
            faultTcs.TrySetResult();
            await coordinator.StopAcceptanceAndNativeAsync();
        }
    }

    [Fact]
    public async Task SettingsQueue_DelayedFaultCallback_DoesNotClearNewConsumersProcessingFlag()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var uiDispatcher = new ImmediateUiDispatcher();
        var delayCallback1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int invokeCount = 0;

        uiDispatcher.CustomInvoke = async callback =>
        {
            invokeCount++;
            if (invokeCount == 2)
            {
                var faultTask = Task.FromException(new InvalidOperationException("Injected dispatch failure for B"));
                _ = Task.Run(async () =>
                {
                    await delayCallback1.Task;
                }, TestContext.Current.CancellationToken);
                await faultTask;
            }
            await callback();
        };

        using var coordinator = new HotkeyCoordinator(
            settingsService,
            hotkeyService,
            Substitute.For<IActionDispatcher>(),
            [new TestActionHandler(IrisAction.InputTranslate)],
            uiDispatcher,
            new FakeNotificationService(),
            NullLogger<HotkeyCoordinator>.Instance);

        try
        {
            await coordinator.StartAsync();

            // Fire B -> fails
            var settingsB = CreateSettingsWithInputKey("B");
            settingsService.Current.Returns(settingsB);
            try
            {
                settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                    settingsService, new SettingsChangedEventArgs(settings, settingsB));
            }
            catch
            {
            }

            // Fire C -> new consumer generation 2 starts and succeeds
            var settingsC = CreateSettingsWithInputKey("C");
            settingsService.Current.Returns(settingsC);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settingsB, settingsC));

            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == 0x43u, TimeSpan.FromSeconds(3));
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x43u);

            // Now release delayed callback 1
            delayCallback1.TrySetResult();

            // Next change D can still be accepted because generation tracking prevented stale callback from corrupting state
            var settingsD = CreateSettingsWithInputKey("D");
            settingsService.Current.Returns(settingsD);
            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService, new SettingsChangedEventArgs(settingsC, settingsD));

            await SpinWaitUntilAsync(() => fakeNative.RegisteredHotkeys.LastOrDefault().Vk == 0x44u, TimeSpan.FromSeconds(3));
            fakeNative.RegisteredHotkeys.Last().Vk.ShouldBe(0x44u);
        }
        finally
        {
            delayCallback1.TrySetResult();
            await coordinator.StopAcceptanceAndNativeAsync();
        }
    }

    private static async Task SpinWaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
            {
                throw new TimeoutException($"Condition was not met within {timeout.TotalMilliseconds}ms.");
            }
            await Task.Delay(10);
        }
    }
}
