using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Platform.Windows.Hotkey;

/// <summary>
/// Platform.Windows 层短时按键录入服务实现。
/// 使用专用消息线程安装 WH_KEYBOARD_LL 低级键盘钩子，构造无 I/O，严格遵守单会话、前台归属与双阶段异步退出。
/// </summary>
internal sealed partial class HotkeyCaptureService : IHotkeyCaptureService
{
    private sealed class DefaultHotkeyBindingPolicy : IHotkeyBindingPolicy
    {
        public bool TryNormalize(HotkeyBinding? binding, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HotkeyBinding? normalized)
        {
            normalized = null;
            if (binding == null)
            {
                return false;
            }

            if (!KeyNameMapper.ValidateBinding(binding, out _))
            {
                return false;
            }

            if (!KeyNameMapper.TryGetCanonicalKeyName(binding.Key, out var canonicalKey))
            {
                return false;
            }

            normalized = binding with { Key = canonicalKey };
            return true;
        }
    }

    private readonly INativeKeyboardHookAdapter _nativeAdapter;
    private readonly IHotkeyBindingPolicy _policy;
    private readonly ILogger<HotkeyCaptureService> _logger;

    private readonly Lock _gate = new();
    private HotkeyCaptureSession? _activeSession;
    private Task? _initTask;
    private Task? _cleanupTask;
    private bool _hasFailedCleanup;
    private bool _isDisposed;

    public HotkeyCaptureService(
        INativeKeyboardHookAdapter? nativeAdapter = null,
        IHotkeyBindingPolicy? policy = null,
        ILogger<HotkeyCaptureService>? logger = null)
    {
        _nativeAdapter = nativeAdapter ?? NativeKeyboardHookAdapter.Instance;
        _policy = policy ?? new DefaultHotkeyBindingPolicy();
        _logger = logger ?? NullLogger<HotkeyCaptureService>.Instance;
    }

    public async Task<IHotkeyCaptureSession> BeginAsync(nint ownerWindowHandle, CancellationToken cancellationToken = default)
    {
        if (ownerWindowHandle == 0 ||
            !_nativeAdapter.IsWindow(ownerWindowHandle) ||
            _nativeAdapter.GetWindowProcessId(ownerWindowHandle) != _nativeAdapter.GetCurrentProcessId())
        {
            throw new IrisException(ErrorCode.InvalidArgument, "无效的设置窗口句柄。");
        }

        if (_nativeAdapter.GetForegroundWindow() != ownerWindowHandle)
        {
            throw new IrisException(ErrorCode.InvalidArgument, "设置窗口不是当前前台活动窗口。");
        }

        HotkeyCaptureSession session;
        TaskCompletionSource<bool> initTcs;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_activeSession != null || _initTask != null || _hasFailedCleanup)
            {
                throw new IrisException(ErrorCode.HotkeyConflict, "当前已有正在进行的按键录入会话。");
            }

            initTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initTask = initTcs.Task;

            session = new HotkeyCaptureSession(
                ownerWindowHandle,
                _nativeAdapter,
                _policy,
                cleanupSession => CleanupSessionAsync(cleanupSession));

            _activeSession = session;
        }

        var thread = new Thread(() => RunMessageThread(session, ownerWindowHandle, initTcs))
        {
            IsBackground = true,
            Name = "Iris.HotkeyCaptureMessageThread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            using var reg = cancellationToken.Register(() =>
            {
                initTcs.TrySetCanceled(cancellationToken);
            });

            await initTcs.Task.ConfigureAwait(false);
            return session;
        }
        catch (OperationCanceledException)
        {
            // 初始化阶段被取消，同步启动原生回滚并完整等待
            session.RequestStop();
            try
            {
                await CleanupSessionAsync(session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRollbackFailed(_logger, ex);
                throw;
            }
            throw;
        }
        catch (Exception ex)
        {
            LogInitFailed(_logger, ex);
            session.RequestStop();
            try
            {
                await CleanupSessionAsync(session).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _initTask = null;
            }
        }
    }

    private void RunMessageThread(
        HotkeyCaptureSession session,
        nint ownerWindowHandle,
        TaskCompletionSource<bool> initTcs)
    {
        uint threadId = _nativeAdapter.GetCurrentThreadId();
        nint hookHandle = 0;

        try
        {
            hookHandle = _nativeAdapter.SetWindowsHook(session.LowLevelKeyboardProc);
            if (hookHandle == 0)
            {
                int err = _nativeAdapter.GetLastError();
                initTcs.TrySetException(new IrisException(ErrorCode.Unknown, $"安装键盘钩子失败 (Win32Error={err})。"));
                session.SignalThreadExit();
                return;
            }

            var initialHeld = DetectInitialHeldKeys();
            session.AttachHook(hookHandle, threadId, initialHeld);
            initTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            initTcs.TrySetException(ex);
            session.SignalThreadExit();
            return;
        }

        try
        {
            while (_nativeAdapter.GetMessage(out var msg))
            {
                if (msg.Message == 0x0012) // WM_QUIT
                {
                    break;
                }
            }
        }
        finally
        {
            session.SignalThreadExit();
        }
    }

    private HashSet<uint> DetectInitialHeldKeys()
    {
        uint[] candidateVks =
        [
            0x11, 0xA2, 0xA3, // Ctrl
            0x12, 0xA4, 0xA5, // Alt
            0x10, 0xA0, 0xA1, // Shift
            0x5B, 0x5C,       // Win
            0x0D, 0x20        // Enter, Space (常用录入启动触发键)
        ];

        var held = new HashSet<uint>();
        foreach (var vk in candidateVks)
        {
            if (_nativeAdapter.IsKeyDown((int)vk))
            {
                held.Add(vk);
            }
        }
        return held;
    }

    public Task StopAsync()
    {
        HotkeyCaptureSession? session;
        lock (_gate)
        {
            session = _activeSession;
        }

        if (session == null)
        {
            return Task.CompletedTask;
        }

        session.RequestStop();
        return CleanupSessionAsync(session);
    }

    private Task CleanupSessionAsync(HotkeyCaptureSession session)
    {
        lock (_gate)
        {
            if (_cleanupTask != null && !_cleanupTask.IsFaulted)
            {
                return _cleanupTask;
            }

            var task = PerformCleanupAsync(session);
            _cleanupTask = task;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cleanupTask, task))
                        {
                            _cleanupTask = null;
                        }
                    }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
            _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }
    }

    private async Task PerformCleanupAsync(HotkeyCaptureSession session)
    {
        try
        {
            // 1. 卸载钩子
            if (session.HookHandle != 0)
            {
                bool unhooked = _nativeAdapter.UnhookWindowsHook(session.HookHandle);
                if (!unhooked)
                {
                    int err = _nativeAdapter.GetLastError();
                    _hasFailedCleanup = true;
                    throw new IrisException(ErrorCode.Unknown, $"卸载键盘钩子失败 (Win32Error={err})。");
                }
            }

            // 2. 发送退出消息终止专用消息线程
            if (session.ThreadId != 0)
            {
                _nativeAdapter.PostThreadQuitMessage(session.ThreadId);
                await session.ThreadExitTask.ConfigureAwait(false);
            }

            lock (_gate)
            {
                _hasFailedCleanup = false;
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }
        }
        catch (Exception ex)
        {
            LogCleanupFailed(_logger, ex);
            lock (_gate)
            {
                _hasFailedCleanup = true;
                _cleanupTask = null; // 允许下次重试
                session.ResetCleanupForRetry();
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (!_hasFailedCleanup)
                {
                    _cleanupTask = null;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_activeSession != null || _hasFailedCleanup)
            {
                throw new InvalidOperationException("录入服务仍持有活跃或未完成清理的原生资源，禁止同步 Dispose，请先调用 StopAsync。");
            }

            _isDisposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _isDisposed = true;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "BeginAsync 取消后的原生回滚失败")]
    private static partial void LogRollbackFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "按键录入消息线程初始化失败")]
    private static partial void LogInitFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "按键录入原生清理失败")]
    private static partial void LogCleanupFailed(ILogger logger, Exception ex);
}
