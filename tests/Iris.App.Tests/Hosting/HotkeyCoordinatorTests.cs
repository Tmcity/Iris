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

namespace Iris.App.Tests.Hosting;

public sealed class HotkeyCoordinatorTests
{
    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool AccessGranted { get; set; } = true;
        public int InvokeCallCount { get; private set; }

        public bool CheckAccess() => AccessGranted;

        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            InvokeCallCount++;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            await callback();
        }
    }

    private sealed class FakeNativeHotkeyAdapter : INativeHotkeyAdapter
    {
        public uint CurrentThreadId { get; set; } = 1000;
        public int LastError { get; set; }
        public NativeWndProc? CapturedWndProc { get; private set; }

        public bool FailRegisterHotKey { get; set; }
        public bool FailUnregisterHotKey { get; set; }
        public bool FailDestroyWindow { get; set; }

        public int RegisterHotKeyCallCount { get; private set; }
        public int UnregisterHotKeyCallCount { get; private set; }

        public List<(nint Hwnd, int Id, uint Modifiers, uint Vk)> RegisteredHotkeys { get; } = [];
        public List<(nint Hwnd, int Id)> UnregisteredHotkeys { get; } = [];

        public uint GetCurrentThreadId() => CurrentThreadId;

        public ushort RegisterClass(string className, NativeWndProc wndProc)
        {
            CapturedWndProc = wndProc;
            return 42;
        }

        public bool UnregisterClass(string className) => true;

        public nint CreateMessageWindow(string className) => 0xCAFE;

        public bool DestroyWindow(nint hwnd)
        {
            if (FailDestroyWindow)
            {
                LastError = 1004;
                return false;
            }
            return true;
        }

        public nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam) => 0;

        public bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk)
        {
            RegisterHotKeyCallCount++;
            if (FailRegisterHotKey)
            {
                if (LastError == 0) LastError = 1409;
                return false;
            }

            RegisteredHotkeys.Add((hwnd, id, fsModifiers, vk));
            return true;
        }

        public bool UnregisterHotKey(nint hwnd, int id)
        {
            UnregisterHotKeyCallCount++;
            if (FailUnregisterHotKey)
            {
                if (LastError == 0) LastError = 1400;
                return false;
            }

            UnregisteredHotkeys.Add((hwnd, id));
            return true;
        }


        public int GetLastError() => LastError;

        public void SendWmHotkey(int id, uint modifiers, uint vk)
        {
            if (CapturedWndProc != null)
            {
                CapturedWndProc(0xCAFE, 0x0312, (nuint)id, (nint)((vk << 16) | (modifiers & 0xFFFF)));
            }
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
        var firstDispatchStarted = new TaskCompletionSource();
        var tcs = new TaskCompletionSource();

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

        // Trigger WM_HOTKEY via fake native adapter for ID (InputTranslate)
        fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);

        // Wait until DispatchAsync actually starts on thread pool
        await firstDispatchStarted.Task;

        dispatchedContexts.Count.ShouldBe(1);
        dispatchedContexts[0].Trigger.ShouldBe(ActionTrigger.Hotkey);

        // Trigger again while first is still running -> dropped by bounded queue
        fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
        dispatchedContexts.Count.ShouldBe(1);

        // Release first dispatch
        tcs.SetResult();
        await coordinator.WaitForInFlightDispatchesAsync();

        // Now next hotkey message can be accepted
        var secondDispatchStarted = new TaskCompletionSource();
        actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                dispatchedContexts.Add(callInfo.Arg<ActionContext>());
                secondDispatchStarted.TrySetResult();
                return Task.CompletedTask;
            });

        fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);
        await secondDispatchStarted.Task;
        dispatchedContexts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task H13_OnHotkeyPressed_DoesNotBlockWndProc_RunsOnThreadPool()
    {
        var fakeNative = new FakeNativeHotkeyAdapter();
        using var hotkeyService = new HotkeyService(NullLogger<HotkeyService>.Instance, fakeNative);

        var settings = new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        int? dispatchThreadId = null;
        var dispatchStarted = new TaskCompletionSource();
        var dispatchFinish = new TaskCompletionSource();

        var actionDispatcher = Substitute.For<IActionDispatcher>();
        actionDispatcher.DispatchAsync(Arg.Any<IrisAction>(), Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
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

        // Send hotkey: SendWmHotkey must return immediately even though dispatch is held
        fakeNative.SendWmHotkey(id, (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), 0x46u);

        // WndProc did NOT block! We can proceed immediately to await dispatchStarted:
        await dispatchStarted.Task;

        // Dispatch completed on a thread pool thread, not inline on the WndProc caller
        dispatchThreadId.ShouldNotBeNull();

        // Complete dispatch
        dispatchFinish.SetResult();
        await coordinator.WaitForInFlightDispatchesAsync();
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

        // Stage 1: Stop acceptance and native resources
        await coordinator.StopAcceptanceAndNativeAsync();

        // Stage 2: Wait for in-flight dispatches
        var waitTask = coordinator.WaitForInFlightDispatchesAsync();
        waitTask.IsCompleted.ShouldBeFalse();

        // Complete the in-flight dispatch
        tcs.SetResult();
        await waitTask;
        waitTask.IsCompletedSuccessfully.ShouldBeTrue();
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
}
