using System;
using System.Collections.Generic;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Settings;

using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Hotkey;

public sealed class HotkeyServiceTests
{
    private sealed class FakeNativeHotkeyAdapter : INativeHotkeyAdapter
    {
        public uint CurrentThreadId { get; set; } = 1000;
        public int LastError { get; set; }
        public NativeWndProc? CapturedWndProc { get; private set; }
        public string? CapturedClassName { get; private set; }

        public int RegisterClassCallCount { get; set; }
        public int UnregisterClassCallCount { get; set; }
        public int CreateMessageWindowCallCount { get; set; }
        public int DestroyWindowCallCount { get; set; }
        public int RegisterHotKeyCallCount { get; set; }
        public int UnregisterHotKeyCallCount { get; set; }
        public int DefWindowProcCallCount { get; set; }

        public bool FailRegisterClass { get; set; }
        public bool FailCreateMessageWindow { get; set; }
        public bool FailRegisterHotKey { get; set; }
        public bool FailUnregisterHotKey { get; set; }
        public HashSet<int> FailSpecificUnregisterIds { get; } = [];
        public bool FailDestroyWindow { get; set; }
        public bool FailUnregisterClass { get; set; }

        public List<(nint Hwnd, int Id, uint Modifiers, uint Vk)> RegisteredHotkeys { get; } = [];
        public HashSet<string> LiveClasses { get; } = new(StringComparer.Ordinal);
        public List<(nint Hwnd, int Id)> UnregisteredHotkeys { get; } = [];

        public uint GetCurrentThreadId() => CurrentThreadId;

        public ushort RegisterClass(string className, NativeWndProc wndProc)
        {
            RegisterClassCallCount++;
            CapturedClassName = className;
            CapturedWndProc = wndProc;
            if (FailRegisterClass)
            {
                LastError = 1001;
                return 0;
            }
            LiveClasses.Add(className);
            return 42;
        }

        public bool UnregisterClass(string className)
        {
            UnregisterClassCallCount++;
            if (FailUnregisterClass)
            {
                LastError = 1002;
                return false;
            }
            LiveClasses.Remove(className);
            return true;
        }

        public nint CreateMessageWindow(string className)
        {
            CreateMessageWindowCallCount++;
            if (FailCreateMessageWindow)
            {
                LastError = 1003;
                return 0;
            }
            return 0xCAFE;
        }

        public bool DestroyWindow(nint hwnd)
        {
            DestroyWindowCallCount++;
            if (FailDestroyWindow)
            {
                LastError = 1004;
                return false;
            }
            return true;
        }

        public nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
        {
            DefWindowProcCallCount++;
            return 0;
        }

        public bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk)
        {
            RegisterHotKeyCallCount++;
            if (FailRegisterHotKey)
            {
                if (LastError == 0) LastError = 1409; // ERROR_HOTKEY_ALREADY_REGISTERED
                return false;
            }

            RegisteredHotkeys.Add((hwnd, id, fsModifiers, vk));
            return true;
        }

        public bool UnregisterHotKey(nint hwnd, int id)
        {
            UnregisterHotKeyCallCount++;
            if (FailUnregisterHotKey || FailSpecificUnregisterIds.Contains(id))
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
            if (CapturedWndProc == null)
            {
                throw new InvalidOperationException("Window procedure not captured.");
            }

            nuint wParam = (nuint)id;
            nint lParam = (nint)((vk << 16) | (modifiers & 0xFFFF));
            CapturedWndProc(0xCAFE, 0x0312, wParam, lParam);
        }
    }

    [Fact]
    public void H01_Constructor_DoesZeroNativeCalls()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        fake.RegisterClassCallCount.ShouldBe(0);
        fake.CreateMessageWindowCallCount.ShouldBe(0);
        fake.RegisterHotKeyCallCount.ShouldBe(0);
    }

    [Fact]
    public void H01_Register_InvalidInput_DoesNotTouchNative_NorModifyExisting()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // 1. Initial valid registration
        var result1 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "F"));
        result1.Success.ShouldBeTrue();
        fake.RegisterHotKeyCallCount.ShouldBe(1);

        // 2. Invalid binding for another action
        var result2 = service.Register(IrisAction.ClipboardTranslate, new HotkeyBinding(HotkeyModifiers.None, "A"));
        result2.Success.ShouldBeFalse();
        result2.Error.ShouldBe(ErrorCode.HotkeyInvalid);
        fake.RegisterHotKeyCallCount.ShouldBe(1); // No new native call

        // 3. Invalid binding for same action does NOT unregister existing
        var result3 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.None, "A"));
        result3.Success.ShouldBeFalse();
        result3.Error.ShouldBe(ErrorCode.HotkeyInvalid);
        fake.UnregisterHotKeyCallCount.ShouldBe(0); // Existing registration untouched
    }

    [Fact]
    public void H01_Register_DisabledBinding_UnregistersExisting_WithoutCheckingKeyValidity()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // Register valid key
        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "F")).Success.ShouldBeTrue();

        // Pass invalid key but Enabled=false -> unregisters successfully without HotkeyInvalid error
        var disabledBinding = new HotkeyBinding(HotkeyModifiers.None, "InvalidKeyName", Enabled: false);
        var result = service.Register(IrisAction.InputTranslate, disabledBinding);

        result.Success.ShouldBeTrue();
        fake.UnregisterHotKeyCallCount.ShouldBe(1);
    }

    [Fact]
    public void H01_Register_DisabledBinding_WhenNotRegistered_ReturnsSuccessWithoutNativeCall()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        var result = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.None, "InvalidKeyName", Enabled: false));
        result.Success.ShouldBeTrue();
        fake.UnregisterHotKeyCallCount.ShouldBe(0);
        fake.RegisterClassCallCount.ShouldBe(0);
    }

    [Fact]
    public void H02_Register_F12_RejectedWithoutNativeCall()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        var result = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "F12"));
        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(ErrorCode.HotkeyInvalid);
        fake.RegisterHotKeyCallCount.ShouldBe(0);
        fake.RegisterClassCallCount.ShouldBe(0);
    }

    [Fact]
    public void H03_Register_AddsModNoRepeat_AndAllocatesMonotonicIds_UnregisterFirst()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // Initial register
        var res1 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res1.Success.ShouldBeTrue();

        fake.RegisteredHotkeys.Count.ShouldBe(1);
        var entry1 = fake.RegisteredHotkeys[0];
        entry1.Id.ShouldBeGreaterThan(0);
        (entry1.Modifiers & 0x4000).ShouldBe(0x4000u); // MOD_NOREPEAT
        (entry1.Modifiers & 0x2).ShouldBe(0x2u); // Control
        entry1.Vk.ShouldBe(0x41u); // A

        // Re-register with different key
        var res2 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Alt, "B"));
        res2.Success.ShouldBeTrue();

        // Must unregister old id before registering new id
        fake.UnregisteredHotkeys.Count.ShouldBe(1);
        fake.UnregisteredHotkeys[0].Id.ShouldBe(entry1.Id);

        fake.RegisteredHotkeys.Count.ShouldBe(2);
        var entry2 = fake.RegisteredHotkeys[1];
        entry2.Id.ShouldBe(entry1.Id + 1); // Monotonic new ID, not reused
        (entry2.Modifiers & 0x1).ShouldBe(0x1u); // Alt
        entry2.Vk.ShouldBe(0x42u); // B
    }

    [Fact]
    public void H03_ProcessLevelIds_AreMonotonicAcrossInstances()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service1 = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);
        using var service2 = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        service1.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A")).Success.ShouldBeTrue();
        service2.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "B")).Success.ShouldBeTrue();

        fake.RegisteredHotkeys.Count.ShouldBe(2);
        int id1 = fake.RegisteredHotkeys[0].Id;
        int id2 = fake.RegisteredHotkeys[1].Id;
        id2.ShouldBeGreaterThan(id1);
    }

    [Fact]
    public void H03_Register_WhenUnregisterOldItemFails_DoesNotRegisterNewItem_AndRetainsOldRecord()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A")).Success.ShouldBeTrue();

        // Simulate Unregister failure
        fake.FailUnregisterHotKey = true;
        fake.LastError = 1400;

        var res = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Alt, "B"));
        res.Success.ShouldBeFalse();
        res.Error.ShouldBe(ErrorCode.Unknown);

        // Did not attempt to call RegisterHotKey for the new key
        fake.RegisterHotKeyCallCount.ShouldBe(1);

        // Later Dispose will retry unregistering the old record
        fake.FailUnregisterHotKey = false;
        service.Dispose();
        fake.UnregisterHotKeyCallCount.ShouldBe(2); // 1 failed during replace, 1 succeeded during dispose
    }

    [Fact]
    public void H03_Register_NativeConflict1409_MapsToHotkeyConflict()
    {
        var fake = new FakeNativeHotkeyAdapter
        {
            FailRegisterHotKey = true,
            LastError = 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
        };
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        var res = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res.Success.ShouldBeFalse();
        res.Error.ShouldBe(ErrorCode.HotkeyConflict);
        res.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.HotkeyConflict));
    }

    [Fact]
    public void H04_WmHotkey_ValidMessage_TriggersPressed_OldOrMismatchedMessage_Discarded()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        var events = new List<IrisAction>();
        service.Pressed += (_, e) => events.Add(e.Action);

        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        int id1 = fake.RegisteredHotkeys[0].Id;

        // 1. Valid matching message -> Triggers Pressed
        fake.SendWmHotkey(id1, (uint)HotkeyModifiers.Control, 0x41u);
        events.Count.ShouldBe(1);
        events[0].ShouldBe(IrisAction.InputTranslate);

        // 2. Unknown ID -> Discarded
        fake.SendWmHotkey(99999, (uint)HotkeyModifiers.Control, 0x41u);
        events.Count.ShouldBe(1);

        // 3. Mismatched VK -> Discarded
        fake.SendWmHotkey(id1, (uint)HotkeyModifiers.Control, 0x42u);
        events.Count.ShouldBe(1);

        // 4. Mismatched Modifiers -> Discarded
        fake.SendWmHotkey(id1, (uint)HotkeyModifiers.Alt, 0x41u);
        events.Count.ShouldBe(1);

        // 5. Re-register Action -> Old ID is retired
        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Alt, "B"));
        int id2 = fake.RegisteredHotkeys[1].Id;

        // Sending old ID -> Discarded
        fake.SendWmHotkey(id1, (uint)HotkeyModifiers.Control, 0x41u);
        events.Count.ShouldBe(1);

        // Sending new ID -> Triggers Pressed
        fake.SendWmHotkey(id2, (uint)HotkeyModifiers.Alt, 0x42u);
        events.Count.ShouldBe(2);
        events[1].ShouldBe(IrisAction.InputTranslate);
    }

    [Fact]
    public void H04_WmHotkey_ExceptionHandler_DoesNotEscapeNativeBoundary()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        service.Pressed += (_, _) => throw new InvalidOperationException("Simulated exception in event handler");

        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        int id = fake.RegisteredHotkeys[0].Id;

        // Must not throw when message arrives
        Should.NotThrow(() => fake.SendWmHotkey(id, (uint)HotkeyModifiers.Control, 0x41u));
    }

    private sealed class IsolatedHotkeyIdAllocator(int startId, int maxId) : IHotkeyIdAllocator
    {
        private int _current = startId - 1;

        public int AllocateId()
        {
            int next = Interlocked.Increment(ref _current);
            if (next > maxId)
            {
                return -1;
            }
            return next;
        }
    }

    [Fact]
    public void H05_IdExhaustion_ReturnsUnknown_AndDoesNotTouchExistingRegistrations()
    {
        var fake = new FakeNativeHotkeyAdapter();
        var allocator = new IsolatedHotkeyIdAllocator(startId: 0xBFFF, maxId: 0xBFFF);
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake, allocator);

        // 1. Allocates 0xBFFF successfully
        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A")).Success.ShouldBeTrue();
        fake.RegisteredHotkeys[0].Id.ShouldBe(0xBFFF);

        // 2. Next attempt exhausts allocator -> returns Unknown error, existing registration untouched
        var res = service.Register(IrisAction.ClipboardTranslate, new HotkeyBinding(HotkeyModifiers.Control, "B"));
        res.Success.ShouldBeFalse();
        res.Error.ShouldBe(ErrorCode.Unknown);

        fake.RegisterHotKeyCallCount.ShouldBe(1);
    }

    [Fact]
    public void H06_UnregisterAll_OneItemFails_KeepsFailedItemAndThrows_RetryCleansUp()
    {
        var fake = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        service.Register(IrisAction.ClipboardTranslate, new HotkeyBinding(HotkeyModifiers.Control, "B"));

        int id1 = fake.RegisteredHotkeys[0].Id;
        int id2 = fake.RegisteredHotkeys[1].Id;

        // Simulate failure on unregistering first ID
        fake.FailSpecificUnregisterIds.Add(id1);

        var ex = Should.Throw<IrisException>(() => service.UnregisterAll());
        ex.Code.ShouldBe(ErrorCode.Unknown);

        // Second ID was unregistered, first ID remains
        fake.UnregisteredHotkeys.Count.ShouldBe(1);
        fake.UnregisteredHotkeys[0].Id.ShouldBe(id2);

        // Retry after clearing failure
        fake.FailSpecificUnregisterIds.Clear();
        Should.NotThrow(() => service.UnregisterAll());
        fake.UnregisteredHotkeys.Count.ShouldBe(2);
        fake.UnregisteredHotkeys[1].Id.ShouldBe(id1);
    }

    [Fact]
    public void H07_InitializeFailures_RegisterClassOrCreateWindowFails_CleansUpAndReturnsError()
    {
        // 1. RegisterClass fails
        var fake1 = new FakeNativeHotkeyAdapter { FailRegisterClass = true };
        using var service1 = new HotkeyService(NullLogger<HotkeyService>.Instance, fake1);

        var res1 = service1.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res1.Success.ShouldBeFalse();
        res1.Error.ShouldBe(ErrorCode.Unknown);
        fake1.RegisterClassCallCount.ShouldBe(1);
        fake1.CreateMessageWindowCallCount.ShouldBe(0);

        // 2. CreateMessageWindow fails (with successful rollback unregister)
        var fake2 = new FakeNativeHotkeyAdapter { FailCreateMessageWindow = true };
        using var service2 = new HotkeyService(NullLogger<HotkeyService>.Instance, fake2);

        var res2 = service2.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res2.Success.ShouldBeFalse();
        res2.Error.ShouldBe(ErrorCode.Unknown);
        fake2.RegisterClassCallCount.ShouldBe(1);
        fake2.CreateMessageWindowCallCount.ShouldBe(1);
        fake2.UnregisterClassCallCount.ShouldBe(1); // Cleans up class on window creation failure
    }

    [Fact]
    public void H07_Initialize_CreateWindowFails_AndUnregisterClassFails_RetainsOwnershipAndAllowsRetry()
    {
        var fake = new FakeNativeHotkeyAdapter
        {
            FailCreateMessageWindow = true,
            FailUnregisterClass = true
        };
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // Window creation fails, and unregistering class rollback also fails
        var res = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res.Success.ShouldBeFalse();
        res.Error.ShouldBe(ErrorCode.Unknown);

        fake.RegisterClassCallCount.ShouldBe(1);
        fake.CreateMessageWindowCallCount.ShouldBe(1);
        fake.UnregisterClassCallCount.ShouldBe(1); // rollback attempted

        // Switching thread and calling Dispose throws -> proves ownership was retained!
        fake.CurrentThreadId = 9999;
        Should.Throw<InvalidOperationException>(() => service.Dispose());

        // Switch back to owner thread, clear FailUnregisterClass, Dispose retries and succeeds!
        fake.CurrentThreadId = 1000;
        fake.FailUnregisterClass = false;
        Should.NotThrow(() => service.Dispose());
        fake.UnregisterClassCallCount.ShouldBe(2); // Retried unregistering class

        // Idempotent second Dispose
        Should.NotThrow(() => service.Dispose());
        fake.UnregisterClassCallCount.ShouldBe(2);
        fake.LiveClasses.Count.ShouldBe(0);
    }

    [Fact]
    public void H07_CreateWindowFails_AndUnregisterFails_RegisterAgain_CrossThread_ThrowsInvalidOperationException()
    {
        var fake = new FakeNativeHotkeyAdapter
        {
            FailCreateMessageWindow = true,
            FailUnregisterClass = true,
            CurrentThreadId = 1000
        };
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // 1. Initial register fails
        var res1 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res1.Success.ShouldBeFalse();
        string? residualClass = service.ClassName;
        residualClass.ShouldNotBeNull();
        fake.LiveClasses.Count.ShouldBe(1);
        service.OwnerThreadId.ShouldBe(1000u);

        // 2. Cross-thread register must be rejected and not overwrite residual class
        fake.CurrentThreadId = 2000;
        Should.Throw<InvalidOperationException>(() =>
            service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "B")));

        service.ClassName.ShouldBe(residualClass);
        service.OwnerThreadId.ShouldBe(1000u);
        fake.LiveClasses.Count.ShouldBe(1);
        fake.RegisterClassCallCount.ShouldBe(1);

        // 3. Switch back to owner thread, clear failure and clean up via Dispose
        fake.CurrentThreadId = 1000;
        fake.FailUnregisterClass = false;
        service.Dispose();
        fake.LiveClasses.Count.ShouldBe(0);
    }

    [Fact]
    public void H07_CreateWindowFails_AndUnregisterFails_RegisterAgain_SameThread_CleansUpResidualOrRejects()
    {
        var fake = new FakeNativeHotkeyAdapter
        {
            FailCreateMessageWindow = true,
            FailUnregisterClass = true,
            CurrentThreadId = 1000
        };
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // 1. Initial register fails, leaves residual class
        var res1 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res1.Success.ShouldBeFalse();
        string? residualClass = service.ClassName;
        residualClass.ShouldNotBeNull();
        fake.LiveClasses.Count.ShouldBe(1);

        // 2. Second register on same thread while UnregisterClass still fails -> rejected without leaking fresh class
        var res2 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "B"));
        res2.Success.ShouldBeFalse();
        service.ClassName.ShouldBe(residualClass);
        fake.RegisterClassCallCount.ShouldBe(1);
        fake.LiveClasses.Count.ShouldBe(1);

        // 3. Third register on same thread with failure cleared -> unregisters residual, registers new class
        fake.FailUnregisterClass = false;
        fake.FailCreateMessageWindow = false;
        var res3 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "C"));
        res3.Success.ShouldBeTrue();
        service.ClassName.ShouldNotBe(residualClass);
        fake.RegisterClassCallCount.ShouldBe(2);
        fake.LiveClasses.Count.ShouldBe(1);

        // 4. Dispose cleans up active class
        service.Dispose();
        fake.LiveClasses.Count.ShouldBe(0);
    }

    [Fact]
    public void H07_Dispose_DestroyWindowSucceeds_UnregisterClassFails_RegisterAgain_SameAndCrossThread()
    {
        var fake = new FakeNativeHotkeyAdapter { CurrentThreadId = 1000 };
        var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // 1. Initial register succeeds
        var res1 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));
        res1.Success.ShouldBeTrue();
        string? originalClass = service.ClassName;
        originalClass.ShouldNotBeNull();
        fake.LiveClasses.Count.ShouldBe(1);

        // 2. Dispose destroys window but fails to unregister class
        fake.FailUnregisterClass = true;
        Should.Throw<IrisException>(() => service.Dispose());
        service.Hwnd.ShouldBe(0);
        service.ClassName.ShouldBe(originalClass);
        service.OwnerThreadId.ShouldBe(1000u);
        fake.LiveClasses.Count.ShouldBe(1);

        // 3. Cross-thread Register rejected
        fake.CurrentThreadId = 2000;
        Should.Throw<InvalidOperationException>(() =>
            service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "B")));

        // 4. Same-thread Register cleans up residual class and reinitializes
        fake.CurrentThreadId = 1000;
        fake.FailUnregisterClass = false;
        var res2 = service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Alt, "C"));
        res2.Success.ShouldBeTrue();
        service.Hwnd.ShouldNotBe(0);
        fake.LiveClasses.Count.ShouldBe(1);

        // 5. Final Dispose cleans up everything
        service.Dispose();
        fake.LiveClasses.Count.ShouldBe(0);
    }

    [Fact]
    public void H07_Dispose_DestroyWindowFails_RetainsResourcesForRetry()
    {
        var fake = new FakeNativeHotkeyAdapter();
        var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A"));

        fake.FailDestroyWindow = true;
        var ex = Should.Throw<IrisException>(() => service.Dispose());
        ex.Code.ShouldBe(ErrorCode.Unknown);

        // Retry Dispose after failure clears
        fake.FailDestroyWindow = false;
        Should.NotThrow(() => service.Dispose());
        fake.DestroyWindowCallCount.ShouldBe(2);
        fake.UnregisterClassCallCount.ShouldBe(1);
    }

    [Fact]
    public void H07_Dispose_Idempotent_WhenUninitializedOrAlreadyDisposed()
    {
        var fake = new FakeNativeHotkeyAdapter();
        var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // Never initialized dispose
        service.Dispose();
        service.Dispose();
        fake.DestroyWindowCallCount.ShouldBe(0);
        fake.UnregisterClassCallCount.ShouldBe(0);

        // Disposed service rejects Register
        Should.Throw<ObjectDisposedException>(() =>
            service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A")));
    }

    [Fact]
    public void H08_OwnerThreadAffinity_EnforcedAcrossMethods()
    {
        var fake = new FakeNativeHotkeyAdapter { CurrentThreadId = 1000 };
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, fake);

        // Initialize on thread 1000
        service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Control, "A")).Success.ShouldBeTrue();

        // Switch to different thread
        fake.CurrentThreadId = 2000;

        Should.Throw<InvalidOperationException>(() =>
            service.Register(IrisAction.InputTranslate, new HotkeyBinding(HotkeyModifiers.Alt, "B")));

        Should.Throw<InvalidOperationException>(() =>
            service.Unregister(IrisAction.InputTranslate));

        Should.Throw<InvalidOperationException>(() =>
            service.UnregisterAll());

        Should.Throw<InvalidOperationException>(() =>
            service.Dispose());

        // Switch back to owner thread -> works!
        fake.CurrentThreadId = 1000;
        Should.NotThrow(() => service.UnregisterAll());
    }
}
