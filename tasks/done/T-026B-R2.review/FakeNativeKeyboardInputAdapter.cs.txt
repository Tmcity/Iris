using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Iris.Platform.Windows.Selection;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Iris.Platform.Windows.Tests.Selection;

internal sealed class FakeNativeKeyboardInputAdapter : INativeKeyboardInputAdapter
{
    public sealed record SendInputRecord(uint CInputs, INPUT Input, int CbSize);

    public int TotalNativeCalls { get; private set; }
    public List<int> AsyncKeyQueries { get; } = [];
    public List<SendInputRecord> RecordedInputs { get; } = [];

    public Dictionary<int, short> KeyStates { get; } = [];
    public nint ForegroundWindow { get; set; } = 0x1234;
    public uint TargetProcessId { get; set; } = 5000;
    public uint CurrentProcessId { get; set; } = 1000;
    public bool WindowIsValid { get; set; } = true;
    public int LastErrorCode { get; set; }

    public Func<uint, INPUT[], int, uint>? CustomSendInputHandler { get; set; }
    public Func<nint>? CustomGetForegroundWindowHandler { get; set; }
    public Func<nint, uint>? CustomGetWindowProcessIdHandler { get; set; }
    public Func<nint, bool>? CustomIsWindowHandler { get; set; }

    public TaskCompletionSource? SendInputEnteredTcs { get; set; }
    public TaskCompletionSource? SendInputReleaseTcs { get; set; }

    private readonly object _queryLock = new();
    private TaskCompletionSource _nextQueryTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForNextKeyQueryAsync(CancellationToken ct = default)
    {
        lock (_queryLock)
        {
            return _nextQueryTcs.Task.WaitAsync(ct);
        }
    }

    public short GetAsyncKeyState(int vKey)
    {
        TotalNativeCalls++;
        AsyncKeyQueries.Add(vKey);

        lock (_queryLock)
        {
            var old = _nextQueryTcs;
            _nextQueryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }

        return KeyStates.TryGetValue(vKey, out var state) ? state : (short)0;
    }

    public nint GetForegroundWindow()
    {
        TotalNativeCalls++;
        if (CustomGetForegroundWindowHandler != null)
        {
            return CustomGetForegroundWindowHandler();
        }
        return ForegroundWindow;
    }

    public uint GetWindowProcessId(nint windowHandle)
    {
        TotalNativeCalls++;
        if (CustomGetWindowProcessIdHandler != null)
        {
            return CustomGetWindowProcessIdHandler(windowHandle);
        }
        return TargetProcessId;
    }

    public uint GetCurrentProcessId()
    {
        TotalNativeCalls++;
        return CurrentProcessId;
    }

    public bool IsWindow(nint windowHandle)
    {
        TotalNativeCalls++;
        if (CustomIsWindowHandler != null)
        {
            return CustomIsWindowHandler(windowHandle);
        }
        return WindowIsValid;
    }

    public uint SendInput(uint cInputs, INPUT[] inputs, int cbSize)
    {
        TotalNativeCalls++;
        if (inputs.Length > 0)
        {
            RecordedInputs.Add(new SendInputRecord(cInputs, inputs[0], cbSize));
        }

        SendInputEnteredTcs?.TrySetResult();
        if (SendInputReleaseTcs != null)
        {
            SendInputReleaseTcs.Task.GetAwaiter().GetResult();
        }

        if (CustomSendInputHandler != null)
        {
            return CustomSendInputHandler(cInputs, inputs, cbSize);
        }

        return cInputs;
    }

    public int GetLastError()
    {
        TotalNativeCalls++;
        return LastErrorCode;
    }
}
