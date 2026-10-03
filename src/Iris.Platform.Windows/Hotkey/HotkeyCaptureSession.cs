using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;

namespace Iris.Platform.Windows.Hotkey;

[StructLayout(LayoutKind.Sequential)]
internal struct KbdLlHookStruct
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

/// <summary>
/// 单次热键录入会话实现。
/// 负责低级键盘事件过滤、按键状态维护、组合有效性判断及 ProgressChanged 的有界合并通知。
/// </summary>
internal sealed class HotkeyCaptureSession : IHotkeyCaptureSession
{
    private readonly nint _ownerWindowHandle;
    private readonly INativeKeyboardHookAdapter _nativeAdapter;
    private readonly IHotkeyBindingPolicy _policy;
    private readonly Func<HotkeyCaptureSession, Task> _cleanupStarter;

    private readonly TaskCompletionSource<HotkeyCaptureResult> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<bool> _threadExitTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<byte> _progressChannel =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });

    private readonly HashSet<uint> _heldKeysAtStart = [];
    private readonly HashSet<uint> _heldModifiers = [];
    private readonly HashSet<uint> _heldNonModifierKeys = [];
    private readonly HashSet<uint> _awaitingReleaseVks = [];

    private readonly Lock _stateLock = new();

    private long _revision;
    private bool _isReady;
    private HotkeyModifiers _activeModifiers;
    private HotkeyCaptureProgress _current;
    private nint _hookHandle;
    private uint _threadId;
    private bool _isStopped;
    private bool _isHookUnhooked;
    private IDisposable? _cancellationRegistration;

    private uint _candidateKeyVk;
    private HotkeyModifiers _candidateModifiers;
    private HotkeyBinding? _candidateBinding;
    private bool _hasConflictInput;

    public Guid Id { get; } = Guid.NewGuid();

    public HotkeyCaptureProgress Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public Task<HotkeyCaptureResult> Completion => _completionTcs.Task;

    public event EventHandler? ProgressChanged;

    internal Task<bool> ThreadExitTask => _threadExitTcs.Task;
    internal nint HookHandle => _hookHandle;
    internal uint ThreadId => _threadId;
    internal bool IsHookUnhooked => Volatile.Read(ref _isHookUnhooked);
    internal bool IsThreadExited => _threadExitTcs.Task.IsCompleted;

    internal bool IsStopRequested
    {
        get
        {
            lock (_stateLock)
            {
                return _isStopped;
            }
        }
    }

    public HotkeyCaptureSession(
        nint ownerWindowHandle,
        INativeKeyboardHookAdapter nativeAdapter,
        IHotkeyBindingPolicy policy,
        Func<HotkeyCaptureSession, Task> cleanupStarter)
    {
        _ownerWindowHandle = ownerWindowHandle;
        _nativeAdapter = nativeAdapter;
        _policy = policy;
        _cleanupStarter = cleanupStarter;

        _current = new HotkeyCaptureProgress(0, false, HotkeyModifiers.None);
        _ = RunProgressWorkerAsync();
    }

    internal void SetHookHandle(nint hookHandle, uint threadId)
    {
        lock (_stateLock)
        {
            _hookHandle = hookHandle;
            _threadId = threadId;
        }
    }

    internal void InitializeHeldKeys(HashSet<uint> initialHeldKeys)
    {
        lock (_stateLock)
        {
            if (initialHeldKeys.Count > 0)
            {
                foreach (var k in initialHeldKeys)
                {
                    _heldKeysAtStart.Add(k);
                }
                _isReady = false;
            }
            else
            {
                _isReady = true;
            }

            _current = new HotkeyCaptureProgress(++_revision, _isReady, HotkeyModifiers.None);
        }

        _progressChannel.Writer.TryWrite(1);
    }

    internal void MarkHookUnhooked()
    {
        Volatile.Write(ref _isHookUnhooked, true);
    }

    internal void RegisterCancellationRegistration(IDisposable registration)
    {
        lock (_stateLock)
        {
            if (_completionTcs.Task.IsCompleted || _isStopped)
            {
                registration.Dispose();
                return;
            }
            _cancellationRegistration = registration;
        }
    }

    internal void SignalThreadExit()
    {
        _threadExitTcs.TrySetResult(true);
    }

    private async Task RunProgressWorkerAsync()
    {
        try
        {
            while (await _progressChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_progressChannel.Reader.TryRead(out _)) { }

                try
                {
                    ProgressChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // 订阅方异常被捕获，不得影响内部通知循环或 native 回调
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    internal void RequestStop()
    {
        lock (_stateLock)
        {
            _isStopped = true;
        }

        Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Canceled));
    }

    internal void Cancel()
    {
        Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Canceled));
    }

    private void Complete(HotkeyCaptureResult result)
    {
        bool completedHere = false;
        lock (_stateLock)
        {
            if (!_completionTcs.Task.IsCompleted)
            {
                _completionTcs.TrySetResult(result);
                completedHere = true;
                _isStopped = true;
            }
        }

        if (completedHere)
        {
            _progressChannel.Writer.TryComplete();
            _cancellationRegistration?.Dispose();
            var task = EnsureCleanupStarted();
            _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private Task? _cleanupTask;

    internal Task EnsureCleanupStarted()
    {
        lock (_stateLock)
        {
            if (_cleanupTask != null && !_cleanupTask.IsFaulted)
            {
                return _cleanupTask;
            }

            var task = _cleanupStarter(this);
            _cleanupTask = task;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_cleanupTask, task))
                        {
                            _cleanupTask = null;
                        }
                    }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }
    }

    internal void ResetCleanupForRetry()
    {
        lock (_stateLock)
        {
            _cleanupTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        var task = EnsureCleanupStarted();
        await task.ConfigureAwait(false);
    }

    internal unsafe nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (nCode < 0)
            {
                return _nativeAdapter.CallNextHook(_hookHandle, nCode, wParam, lParam);
            }

            // 窗口丢失前台校验：若前台不再是目标设置窗口，立即停止捕获并取消会话，新按键不消费、按原链传递
            var fg = _nativeAdapter.GetForegroundWindow();
            if (fg != _ownerWindowHandle)
            {
                Cancel();
                return _nativeAdapter.CallNextHook(_hookHandle, nCode, wParam, lParam);
            }

            bool stopped;
            lock (_stateLock)
            {
                stopped = _isStopped || _completionTcs.Task.IsCompleted;
            }

            if (stopped || lParam == 0)
            {
                return _nativeAdapter.CallNextHook(_hookHandle, nCode, wParam, lParam);
            }

            var pKbd = (KbdLlHookStruct*)(void*)lParam;
            uint vk = pKbd->vkCode;

            bool isKeyDown = (wParam == 0x0100 || wParam == 0x0104);
            bool isKeyUp = (wParam == 0x0101 || wParam == 0x0105);

            if (!isKeyDown && !isKeyUp)
            {
                return _nativeAdapter.CallNextHook(_hookHandle, nCode, wParam, lParam);
            }

            bool isModifier = IsModifierKey(vk, out var modFlag);
            bool notifyChanged = false;
            bool shouldConsume = false;

            lock (_stateLock)
            {
                // 1. 准备阶段（请松开启动键）
                if (!_isReady)
                {
                    if (isKeyUp)
                    {
                        _heldKeysAtStart.Remove(vk);
                        if (_heldKeysAtStart.Count == 0)
                        {
                            _isReady = true;
                            _current = new HotkeyCaptureProgress(++_revision, true, HotkeyModifiers.None);
                            notifyChanged = true;
                        }
                        shouldConsume = true;
                    }
                    else
                    {
                        // 准备期间新按下的键不生成候选，消费处理
                        shouldConsume = true;
                    }
                }
                // 2. 正常录入阶段
                else
                {
                    if (isKeyDown)
                    {
                        if (isModifier)
                        {
                            _heldModifiers.Add(vk);
                            _activeModifiers = CalculateModifiersFromHeld(_heldModifiers);

                            if (_candidateBinding == null && !_hasConflictInput)
                            {
                                _current = new HotkeyCaptureProgress(++_revision, true, _activeModifiers, Key: null);
                                notifyChanged = true;
                            }

                            shouldConsume = true;
                        }
                        else
                        {
                            // 无修饰 Escape 取消本次录入
                            if (vk == 0x1B && _activeModifiers == HotkeyModifiers.None)
                            {
                                Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Canceled));
                                return 1;
                            }

                            // 无修饰 Backspace 清除为禁用空键
                            if (vk == 0x08 && _activeModifiers == HotkeyModifiers.None)
                            {
                                Complete(new HotkeyCaptureResult(
                                    HotkeyCaptureOutcome.Cleared,
                                    new HotkeyBinding(HotkeyModifiers.None, string.Empty, false)));
                                return 1;
                            }

                            // 重复按键去重：若是当前候选主键的 auto-repeat，直接去重忽略并消费
                            if (_candidateKeyVk == vk)
                            {
                                shouldConsume = true;
                            }
                            else
                            {
                                _heldNonModifierKeys.Add(vk);

                                if (_candidateKeyVk != 0 || _heldNonModifierKeys.Count > 1)
                                {
                                    _hasConflictInput = true;
                                    _candidateBinding = null;
                                    _candidateKeyVk = 0;
                                    _awaitingReleaseVks.Clear();
                                    _current = new HotkeyCaptureProgress(
                                        ++_revision,
                                        true,
                                        _activeModifiers,
                                        Key: null,
                                        Error: new ErrorInfo(ErrorCode.HotkeyInvalid, "多个按键同时按下，请松开后重试"));
                                    notifyChanged = true;
                                    shouldConsume = true;
                                }
                                else if (_hasConflictInput)
                                {
                                    shouldConsume = true;
                                }
                                else
                                {
                                    // 首次按下主键，锁定此刻的修饰键快照
                                    _candidateKeyVk = vk;
                                    _candidateModifiers = _activeModifiers;

                                    _awaitingReleaseVks.Clear();
                                    _awaitingReleaseVks.Add(vk);
                                    foreach (var m in _heldModifiers)
                                    {
                                        _awaitingReleaseVks.Add(m);
                                    }

                                    if (KeyNameMapper.TryMapFromVk(vk, out var canonicalKey))
                                    {
                                        var rawBinding = new HotkeyBinding(_candidateModifiers, canonicalKey, true);
                                        if (_policy.TryNormalize(rawBinding, out var normalized))
                                        {
                                            _candidateBinding = normalized;
                                            _current = new HotkeyCaptureProgress(
                                                ++_revision,
                                                true,
                                                normalized.Modifiers,
                                                Key: normalized.Key,
                                                Error: null);
                                        }
                                        else
                                        {
                                            _candidateBinding = null;
                                            _current = new HotkeyCaptureProgress(
                                                ++_revision,
                                                true,
                                                _candidateModifiers,
                                                Key: null,
                                                Error: new ErrorInfo(ErrorCode.HotkeyInvalid, "无效的快捷键组合"));
                                        }
                                    }
                                    else
                                    {
                                        _candidateBinding = null;
                                        _current = new HotkeyCaptureProgress(
                                            ++_revision,
                                            true,
                                            _candidateModifiers,
                                            Key: null,
                                            Error: new ErrorInfo(ErrorCode.HotkeyInvalid, "未知按键"));
                                    }

                                    notifyChanged = true;
                                    shouldConsume = true;
                                }
                            }
                        }
                    }
                    else if (isKeyUp)
                    {
                        if (isModifier)
                        {
                            _heldModifiers.Remove(vk);
                            _activeModifiers = CalculateModifiersFromHeld(_heldModifiers);

                            if (_hasConflictInput)
                            {
                                if (_heldModifiers.Count == 0 && _heldNonModifierKeys.Count == 0)
                                {
                                    _hasConflictInput = false;
                                    _current = new HotkeyCaptureProgress(++_revision, true, HotkeyModifiers.None);
                                    notifyChanged = true;
                                }
                            }
                            else if (_candidateBinding != null)
                            {
                                _awaitingReleaseVks.Remove(vk);
                                if (_awaitingReleaseVks.Count == 0)
                                {
                                    var captured = _candidateBinding;
                                    Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Captured, Binding: captured));
                                    return 1;
                                }
                            }

                            shouldConsume = true;
                        }
                        else
                        {
                            _heldNonModifierKeys.Remove(vk);

                            if (vk == _candidateKeyVk)
                            {
                                _awaitingReleaseVks.Remove(vk);

                                if (_candidateBinding != null && _awaitingReleaseVks.Count == 0)
                                {
                                    var captured = _candidateBinding;
                                    Complete(new HotkeyCaptureResult(HotkeyCaptureOutcome.Captured, Binding: captured));
                                    return 1;
                                }

                                if (_candidateBinding == null)
                                {
                                    _candidateKeyVk = 0;
                                }
                            }

                            if (_hasConflictInput)
                            {
                                if (_heldModifiers.Count == 0 && _heldNonModifierKeys.Count == 0)
                                {
                                    _hasConflictInput = false;
                                    _current = new HotkeyCaptureProgress(++_revision, true, HotkeyModifiers.None);
                                    notifyChanged = true;
                                }
                            }

                            shouldConsume = true;
                        }
                    }
                }
            }

            if (notifyChanged)
            {
                _progressChannel.Writer.TryWrite(1);
            }

            if (shouldConsume)
            {
                return 1;
            }

            return _nativeAdapter.CallNextHook(_hookHandle, nCode, wParam, lParam);
        }
        catch (Exception)
        {
            return _nativeAdapter.CallNextHook(_hookHandle, nCode, wParam, lParam);
        }
    }

    private static bool IsModifierKey(uint vk, out HotkeyModifiers mod)
    {
        mod = HotkeyModifiers.None;
        if (vk is 0x11 or 0xA2 or 0xA3) // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        {
            mod = HotkeyModifiers.Control;
            return true;
        }
        if (vk is 0x12 or 0xA4 or 0xA5) // VK_MENU, VK_LMENU, VK_RMENU (Alt)
        {
            mod = HotkeyModifiers.Alt;
            return true;
        }
        if (vk is 0x10 or 0xA0 or 0xA1) // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        {
            mod = HotkeyModifiers.Shift;
            return true;
        }
        if (vk is 0x5B or 0x5C) // VK_LWIN, VK_RWIN
        {
            mod = HotkeyModifiers.Win;
            return true;
        }

        return false;
    }

    private static HotkeyModifiers CalculateModifiersFromHeld(HashSet<uint> held)
    {
        var result = HotkeyModifiers.None;
        foreach (var vk in held)
        {
            if (IsModifierKey(vk, out var m))
            {
                result |= m;
            }
        }
        return result;
    }
}
