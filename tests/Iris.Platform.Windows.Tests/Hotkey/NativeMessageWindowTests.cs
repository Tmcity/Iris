using System;
using Iris.Platform.Windows.Hotkey;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Hotkey;

public sealed class NativeMessageWindowTests
{
    [Fact]
    public void NativeHotkeyAdapter_CreatesAndDestroys_MessageWindow()
    {
        var adapter = NativeHotkeyAdapter.Instance;
        string className = $"Iris.TestWindow.{Guid.NewGuid():N}";

        nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam) =>
            adapter.DefWindowProc(hwnd, msg, wParam, lParam);

        ushort atom = adapter.RegisterClass(className, WndProc);
        atom.ShouldNotBe((ushort)0);

        nint hwnd = 0;
        try
        {
            hwnd = adapter.CreateMessageWindow(className);
            hwnd.ShouldNotBe(0);

            bool destroyed = adapter.DestroyWindow(hwnd);
            destroyed.ShouldBeTrue();
            hwnd = 0;
        }
        finally
        {
            if (hwnd != 0)
            {
                adapter.DestroyWindow(hwnd);
            }

            bool unregistered = adapter.UnregisterClass(className);
            unregistered.ShouldBeTrue();
        }
    }

    [Fact]
    public void NativeHotkeyAdapter_UnregisterClass_RetainsDelegateWhenUnregisterFails()
    {
        var adapter = NativeHotkeyAdapter.Instance;
        string className = $"Iris.TestWindow.{Guid.NewGuid():N}";

        nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam) =>
            adapter.DefWindowProc(hwnd, msg, wParam, lParam);

        ushort atom = adapter.RegisterClass(className, WndProc);
        atom.ShouldNotBe((ushort)0);
        adapter.HasActiveDelegate(className).ShouldBeTrue();

        nint hwnd = adapter.CreateMessageWindow(className);
        hwnd.ShouldNotBe(0);

        try
        {
            // When an active window exists, Win32 UnregisterClass fails
            bool failedUnregister = adapter.UnregisterClass(className);
            failedUnregister.ShouldBeFalse();

            // Delegate is still kept because unregister failed!
            adapter.HasActiveDelegate(className).ShouldBeTrue();
        }
        finally
        {
            adapter.DestroyWindow(hwnd);

            // Once window is destroyed, UnregisterClass succeeds and delegate is removed
            bool successUnregister = adapter.UnregisterClass(className);
            successUnregister.ShouldBeTrue();
            adapter.HasActiveDelegate(className).ShouldBeFalse();
        }
    }
}
