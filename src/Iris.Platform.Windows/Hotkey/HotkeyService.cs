using System;
using System.Collections.Generic;
using System.Linq;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Hotkey;

/// <summary>
/// Win32 全局热键服务实现。完整规则见 HOTKEY_SERVICE.md 与 CCR-014。
/// 构造无 I/O，首次有效启用注册由 App 在 UI 线程创建消息窗口并绑定所有者线程。
/// </summary>
public sealed partial class HotkeyService : IHotkeyService
{
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int MaxAllowedHotkeyId = 0xBFFF;

    private readonly ILogger<HotkeyService> _logger;
    private readonly INativeHotkeyAdapter _adapter;
    private readonly IHotkeyIdAllocator _idAllocator;
    private readonly NativeWndProc _wndProcDelegate;

    private readonly Dictionary<IrisAction, HotkeyRegistrationEntry> _registrationsByAction = [];
    private readonly Dictionary<int, HotkeyRegistrationEntry> _registrationsById = [];

    private uint? _ownerThreadId;
    private nint _hwnd;
    private ushort _classAtom;
    private string? _className;
    private bool _disposed;

    public event EventHandler<HotkeyPressedEventArgs>? Pressed;

    public HotkeyService(ILogger<HotkeyService> logger)
        : this(logger, NativeHotkeyAdapter.Instance, DefaultHotkeyIdAllocator.Instance)
    {
    }

    internal HotkeyService(
        ILogger<HotkeyService> logger,
        INativeHotkeyAdapter adapter,
        IHotkeyIdAllocator? idAllocator = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _idAllocator = idAllocator ?? DefaultHotkeyIdAllocator.Instance;
        _wndProcDelegate = WndProc;
    }

    public HotkeyRegistrationResult Register(IrisAction action, HotkeyBinding binding)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "未定义的 IrisAction。");
        }
        ArgumentNullException.ThrowIfNull(binding);

        ObjectDisposedException.ThrowIf(_disposed, typeof(HotkeyService));

        if (_ownerThreadId.HasValue)
        {
            EnsureOwnerThread();
        }

        // 1. Enabled=false 为注销语义，不校验 Key 的可注册性
        if (!binding.Enabled)
        {
            if (_registrationsByAction.TryGetValue(action, out var existing))
            {
                existing.IsValid = false; // 立即撤销分发资格
                if (!_adapter.UnregisterHotKey(_hwnd, existing.Id))
                {
                    int err = _adapter.GetLastError();
                    LogUnregisterDisabledFailed(_logger, action, existing.Id, err);
                    return new HotkeyRegistrationResult(action, false, ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
                }

                _registrationsByAction.Remove(action);
                _registrationsById.Remove(existing.Id);
            }

            return new HotkeyRegistrationResult(action, true);
        }

        // 2. 校验输入键名、修饰位及组合规则（含 F12 拦截）
        if (!KeyNameMapper.ValidateBinding(binding, out uint vk))
        {
            return new HotkeyRegistrationResult(action, false, ErrorCode.HotkeyInvalid, ErrorMessages.GetDefault(ErrorCode.HotkeyInvalid));
        }

        // 3. 检查 ID 耗尽（先于注销旧项，耗尽时不触碰当前有效注册）
        int newId = _idAllocator.AllocateId();
        if (newId <= 0)
        {
            LogIdExhausted(_logger);
            return new HotkeyRegistrationResult(action, false, ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
        }

        // 4. 首次有效且启用注册时初始化原生消息窗口并绑定所有者线程
        if (_hwnd == 0)
        {
            if (!InitializeMessageWindow())
            {
                return new HotkeyRegistrationResult(action, false, ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
            }
        }

        // 5. 对已有 Action 先注销再注册；若注销失败，保留旧记录用于重试，不叠加新注册
        if (_registrationsByAction.TryGetValue(action, out var currentEntry))
        {
            currentEntry.IsValid = false; // 立即撤销分发资格
            if (!_adapter.UnregisterHotKey(_hwnd, currentEntry.Id))
            {
                int err = _adapter.GetLastError();
                LogUnregisterReplaceFailed(_logger, action, currentEntry.Id, err);
                return new HotkeyRegistrationResult(action, false, ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
            }

            _registrationsByAction.Remove(action);
            _registrationsById.Remove(currentEntry.Id);
        }

        // 6. 分配单调递增新 ID 并调用原生注册（加入 MOD_NOREPEAT）
        uint fsModifiers = (uint)binding.Modifiers | ModNoRepeat;
        bool registered = _adapter.RegisterHotKey(_hwnd, newId, fsModifiers, vk);

        if (registered)
        {
            var entry = new HotkeyRegistrationEntry(newId, action, binding.Modifiers, vk, isValid: true);
            _registrationsByAction[action] = entry;
            _registrationsById[newId] = entry;
            return new HotkeyRegistrationResult(action, true);
        }

        int lastError = _adapter.GetLastError();
        ErrorCode code = lastError == 1409 ? ErrorCode.HotkeyConflict : ErrorCode.Unknown;
        LogRegisterFailed(_logger, action, vk, (uint)binding.Modifiers, lastError);

        return new HotkeyRegistrationResult(action, false, code, ErrorMessages.GetDefault(code));
    }

    public void Unregister(IrisAction action)
    {
        if (_ownerThreadId.HasValue)
        {
            EnsureOwnerThread();
        }

        if (_hwnd == 0)
        {
            return;
        }

        if (_registrationsByAction.TryGetValue(action, out var entry))
        {
            entry.IsValid = false;
            if (_adapter.UnregisterHotKey(_hwnd, entry.Id))
            {
                _registrationsByAction.Remove(action);
                _registrationsById.Remove(entry.Id);
            }
            else
            {
                int err = _adapter.GetLastError();
                LogUnregisterFailed(_logger, action, entry.Id, err);
                throw new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
            }
        }
    }

    public void UnregisterAll()
    {
        if (_ownerThreadId.HasValue)
        {
            EnsureOwnerThread();
        }

        if (_hwnd == 0)
        {
            return;
        }

        List<int> failedIds = [];
        foreach (var entry in _registrationsByAction.Values.ToList())
        {
            entry.IsValid = false;
            if (_adapter.UnregisterHotKey(_hwnd, entry.Id))
            {
                _registrationsByAction.Remove(entry.Action);
                _registrationsById.Remove(entry.Id);
            }
            else
            {
                failedIds.Add(entry.Id);
                LogUnregisterAllItemFailed(_logger, entry.Action, entry.Id, _adapter.GetLastError());
            }
        }

        if (failedIds.Count > 0)
        {
            throw new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
        }
    }

    public void Dispose()
    {
        if (_hwnd == 0 && _classAtom == 0)
        {
            _disposed = true;
            return;
        }

        EnsureOwnerThread();

        // 1. 先完成热键注销
        UnregisterAll();

        // 2. 销毁自有消息窗口
        if (_hwnd != 0)
        {
            if (!_adapter.DestroyWindow(_hwnd))
            {
                int err = _adapter.GetLastError();
                LogDestroyWindowFailed(_logger, _hwnd, err);
                throw new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
            }
            _hwnd = 0;
        }

        // 3. 注销窗口类
        if (_classAtom != 0 && _className != null)
        {
            if (!_adapter.UnregisterClass(_className))
            {
                int err = _adapter.GetLastError();
                LogUnregisterClassFailed(_logger, _className, err);
                throw new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
            }
            _classAtom = 0;
            _className = null;
        }

        _disposed = true;
        _ownerThreadId = null;
    }

    internal string? ClassName => _className;
    internal ushort ClassAtom => _classAtom;
    internal uint? OwnerThreadId => _ownerThreadId;
    internal nint Hwnd => _hwnd;

    private bool InitializeMessageWindow()
    {
        if (_ownerThreadId.HasValue)
        {
            EnsureOwnerThread();
        }

        if (_className != null)
        {
            if (_adapter.UnregisterClass(_className))
            {
                _className = null;
                _classAtom = 0;
                _ownerThreadId = null;
            }
            else
            {
                int err = _adapter.GetLastError();
                LogUnregisterClassFailed(_logger, _className, err);
                return false;
            }
        }

        uint threadId = _adapter.GetCurrentThreadId();
        string name = $"Iris.HotkeyWindow.{Guid.NewGuid():N}";

        ushort atom = _adapter.RegisterClass(name, _wndProcDelegate);
        if (atom == 0)
        {
            int err = _adapter.GetLastError();
            LogRegisterClassFailed(_logger, err);
            return false;
        }

        _className = name;
        _classAtom = atom;
        _ownerThreadId = threadId;

        nint hwnd = _adapter.CreateMessageWindow(name);
        if (hwnd == 0)
        {
            int err = _adapter.GetLastError();
            LogCreateMessageWindowFailed(_logger, err);

            if (_adapter.UnregisterClass(name))
            {
                _className = null;
                _classAtom = 0;
                _ownerThreadId = null;
            }
            else
            {
                int unregErr = _adapter.GetLastError();
                LogUnregisterClassFailed(_logger, name, unregErr);
            }

            return false;
        }

        _hwnd = hwnd;
        return true;
    }

    private void EnsureOwnerThread()
    {
        if (_ownerThreadId.HasValue && _adapter.GetCurrentThreadId() != _ownerThreadId.Value)
        {
            throw new InvalidOperationException("Cross-thread operation on HotkeyService is not allowed. Native resources belong to the thread that initialized them.");
        }
    }

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (msg == WmHotkey)
            {
                HandleWmHotkey((int)wParam, lParam);
                return 0;
            }

            return _adapter.DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            LogWndProcException(_logger, ex.GetType().Name);
            return 0;
        }
    }

    private void HandleWmHotkey(int id, nint lParam)
    {
        if (_disposed)
        {
            return;
        }

        if (!_registrationsById.TryGetValue(id, out var entry) || !entry.IsValid)
        {
            return;
        }

        uint msgModifiers = (uint)(lParam & 0xFFFF) & (0x1 | 0x2 | 0x4 | 0x8);
        uint msgVk = (uint)((lParam >> 16) & 0xFFFF);

        if ((uint)entry.Modifiers != msgModifiers || entry.Vk != msgVk)
        {
            return;
        }

        try
        {
            Pressed?.Invoke(this, new HotkeyPressedEventArgs(entry.Action));
        }
        catch (Exception ex)
        {
            LogPressedException(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "UnregisterHotKey failed for disabled action: Action={Action}, Id={Id}, NativeError={ErrorCode}")]
    private static partial void LogUnregisterDisabledFailed(ILogger logger, IrisAction action, int id, int errorCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Hotkey registration ID exhausted")]
    private static partial void LogIdExhausted(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "UnregisterHotKey failed during replace: Action={Action}, Id={Id}, NativeError={ErrorCode}")]
    private static partial void LogUnregisterReplaceFailed(ILogger logger, IrisAction action, int id, int errorCode);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "RegisterHotKey failed: Action={Action}, VK=0x{Vk:X2}, Modifiers=0x{Modifiers:X}, NativeError={ErrorCode}")]
    private static partial void LogRegisterFailed(ILogger logger, IrisAction action, uint vk, uint modifiers, int errorCode);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "UnregisterHotKey failed: Action={Action}, Id={Id}, NativeError={ErrorCode}")]
    private static partial void LogUnregisterFailed(ILogger logger, IrisAction action, int id, int errorCode);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "UnregisterHotKey failed in UnregisterAll: Action={Action}, Id={Id}, NativeError={ErrorCode}")]
    private static partial void LogUnregisterAllItemFailed(ILogger logger, IrisAction action, int id, int errorCode);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "DestroyWindow failed in Dispose: HWND={Hwnd}, NativeError={ErrorCode}")]
    private static partial void LogDestroyWindowFailed(ILogger logger, nint hwnd, int errorCode);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "UnregisterClass failed in Dispose: ClassName={ClassName}, NativeError={ErrorCode}")]
    private static partial void LogUnregisterClassFailed(ILogger logger, string className, int errorCode);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "RegisterClass failed during window init: NativeError={ErrorCode}")]
    private static partial void LogRegisterClassFailed(ILogger logger, int errorCode);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "CreateMessageWindow failed during window init: NativeError={ErrorCode}")]
    private static partial void LogCreateMessageWindowFailed(ILogger logger, int errorCode);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Unhandled exception in WndProc: {ExceptionType}")]
    private static partial void LogWndProcException(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Exception in Pressed event handler: {ExceptionType}")]
    private static partial void LogPressedException(ILogger logger, string exceptionType);

    private sealed class HotkeyRegistrationEntry(int id, IrisAction action, HotkeyModifiers modifiers, uint vk, bool isValid)
    {
        public int Id { get; } = id;
        public IrisAction Action { get; } = action;
        public HotkeyModifiers Modifiers { get; } = modifiers;
        public uint Vk { get; } = vk;
        public bool IsValid { get; set; } = isValid;
    }
}
