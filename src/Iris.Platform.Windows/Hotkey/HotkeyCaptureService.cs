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

        cancellationToken.ThrowIfCancellationRequested();

        HotkeyCaptureSession session;
        TaskCompletionSource<bool> physicalInitTcs;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_activeSession != null || _initTask != null || _hasFailedCleanup || (_cleanupTask != null && !_cleanupTask.IsCompleted))
            {
                throw new IrisException(ErrorCode.HotkeyConflict, "当前已有正在进行的按键录入会话。");
            }

            physicalInitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initTask = physicalInitTcs.Task;

            session = new HotkeyCaptureSession(
                ownerWindowHandle,
                _nativeAdapter,
                _policy,
                cleanupSession => CleanupSessionAsync(cleanupSession));

            _activeSession = session;
        }

        if (cancellationToken.CanBeCanceled)
        {
            var reg = cancellationToken.Register(() =>
            {
                session.Cancel();
                session.RequestStop();
            });
            session.RegisterCancellationRegistration(reg);
        }

        var thread = new Thread(() => RunMessageThread(session, physicalInitTcs))
        {
            IsBackground = true,
            Name = "Iris.HotkeyCaptureMessageThread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            // 物理初始化屏障：等待线程真实完成 SetWindowsHook 并登记句柄（或失败）
            await physicalInitTcs.Task.ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || session.IsStopRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            session.RequestStop();
            try
            {
                await CleanupSessionAsync(session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRollbackFailed(_logger, ex.GetType().Name);
                throw;
            }
            throw;
        }
        catch (Exception ex)
        {
            LogInitFailed(_logger, ex.GetType().Name);
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
                if (ReferenceEquals(_initTask, physicalInitTcs.Task))
                {
                    _initTask = null;
                }
            }
        }
    }

    private void RunMessageThread(
        HotkeyCaptureSession session,
        TaskCompletionSource<bool> physicalInitTcs)
    {
        uint threadId = _nativeAdapter.GetCurrentThreadId();
        nint hookHandle = 0;

        try
        {
            _nativeAdapter.EnsureThreadMessageQueueCreated();

            hookHandle = _nativeAdapter.SetWindowsHook(session.LowLevelKeyboardProc);
            if (hookHandle == 0)
            {
                int err = _nativeAdapter.GetLastError();
                physicalInitTcs.TrySetException(new IrisException(ErrorCode.Unknown, $"安装键盘钩子失败 (Win32Error={err})。"));
                session.SignalThreadExit();
                return;
            }

            session.SetHookHandle(hookHandle, threadId);

            if (session.IsStopRequested)
            {
                // 已请求停止或取消：登记句柄后完成物理屏障，由后续清理协议执行回滚
                physicalInitTcs.TrySetResult(true);
                session.SignalThreadExit();
                return;
            }

            var initialHeld = DetectInitialHeldKeys();
            session.InitializeHeldKeys(initialHeld);
            physicalInitTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            physicalInitTcs.TrySetException(ex);
            session.SignalThreadExit();
            return;
        }

        try
        {
            while (true)
            {
                int ret = _nativeAdapter.GetMessage(out var msg);
                if (ret < 0)
                {
                    int err = _nativeAdapter.GetLastError();
                    LogMessageLoopError(_logger, $"GetMessage failed (Win32Error={err})");
                    session.Fail(new IrisException(ErrorCode.Unknown, "消息循环异常终止。"));
                    _ = CleanupSessionAsync(session);
                    break;
                }
                if (ret == 0 || msg.Message == 0x0012) // WM_QUIT
                {
                    if (!session.IsStopRequested)
                    {
                        LogMessageLoopError(_logger, "Unexpected WM_QUIT received before stop requested");
                        session.Fail(new IrisException(ErrorCode.Unknown, "录入消息循环意外退出。"));
                        _ = CleanupSessionAsync(session);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogMessageLoopError(_logger, ex.GetType().Name);
            session.Fail(new IrisException(ErrorCode.Unknown, "录入消息循环发生异常。"));
            _ = CleanupSessionAsync(session);
        }
        finally
        {
            session.SignalThreadExit();
        }
    }

    private HashSet<uint> DetectInitialHeldKeys()
    {
        uint[] candidatePhysicalVks =
        [
            0xA2, 0xA3, // VK_LCONTROL, VK_RCONTROL
            0xA4, 0xA5, // VK_LMENU, VK_RMENU
            0xA0, 0xA1, // VK_LSHIFT, VK_RSHIFT
            0x5B, 0x5C, // VK_LWIN, VK_RWIN
            0x0D, 0x20  // VK_RETURN, VK_SPACE
        ];

        var held = new HashSet<uint>();
        foreach (var vk in candidatePhysicalVks)
        {
            if (_nativeAdapter.IsKeyDown((int)vk))
            {
                held.Add(vk);
            }
        }
        return held;
    }

    public async Task StopAsync()
    {
        Task? initTask;
        HotkeyCaptureSession? session;
        lock (_gate)
        {
            initTask = _initTask;
            session = _activeSession;
        }

        if (session == null && initTask == null && !_hasFailedCleanup)
        {
            return;
        }

        session?.RequestStop();

        if (initTask != null)
        {
            try
            {
                await initTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        lock (_gate)
        {
            session = _activeSession;
        }

        if (session != null)
        {
            await CleanupSessionAsync(session).ConfigureAwait(false);
        }
    }

    private Task CleanupSessionAsync(HotkeyCaptureSession session)
    {
        lock (_gate)
        {
            if (_cleanupTask != null && !_cleanupTask.IsCompleted)
            {
                return _cleanupTask;
            }

            var task = PerformCleanupAsync(session);
            _cleanupTask = task;
            return task;
        }
    }

    private async Task PerformCleanupAsync(HotkeyCaptureSession session)
    {
        // 物理初始化屏障：必须先等待初始化真实结束（无论成功或失败），确保句柄与线程完整登记
        Task? initTask;
        lock (_gate)
        {
            initTask = _initTask;
        }

        if (initTask != null)
        {
            try
            {
                await initTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        nint hookHandle = session.HookHandle;
        try
        {
            // 1. 卸载钩子
            if (hookHandle != 0 && !session.IsHookUnhooked)
            {
                bool unhooked = _nativeAdapter.UnhookWindowsHook(hookHandle);
                if (!unhooked)
                {
                    int err = _nativeAdapter.GetLastError();
                    _hasFailedCleanup = true;
                    throw new IrisException(ErrorCode.Unknown, $"卸载键盘钩子失败 (Win32Error={err})。");
                }
                session.MarkHookUnhooked();
            }

            // 2. 发送退出消息终止专用消息线程并等待其退出
            if (session.ThreadId != 0 && !session.IsThreadExited)
            {
                bool posted = _nativeAdapter.PostThreadQuitMessage(session.ThreadId);
                if (!posted)
                {
                    int err = _nativeAdapter.GetLastError();
                    _hasFailedCleanup = true;
                    throw new IrisException(ErrorCode.Unknown, $"向录入消息线程发送退出消息失败 (Win32Error={err})。");
                }
                await session.ThreadExitTask.ConfigureAwait(false);
            }

            // 3. 线程退出屏障完成后，才释放托管委托根
            if (hookHandle != 0 && !session.IsHookResourcesReleased)
            {
                _nativeAdapter.ReleaseHookResources(hookHandle);
                session.MarkHookResourcesReleased();
            }

            // 4. 等待通知 worker 和进度订阅者完成退出
            await session.WaitForProgressWorkerAsync().ConfigureAwait(false);

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
            LogCleanupFailed(_logger, ex.GetType().Name);
            lock (_gate)
            {
                _hasFailedCleanup = true;
            }
            session.ResetCleanupForRetry();
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

            if (_activeSession != null || _hasFailedCleanup || _initTask != null)
            {
                throw new InvalidOperationException("录入服务仍持有活跃或未完成清理的原生资源，禁止同步 Dispose，请先调用 StopAsync。");
            }

            _isDisposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _isDisposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "BeginAsync 取消后的原生回滚失败 (ExceptionType={ExceptionType})")]
    private static partial void LogRollbackFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "按键录入消息线程初始化失败 (ExceptionType={ExceptionType})")]
    private static partial void LogInitFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "按键录入原生清理失败 (ExceptionType={ExceptionType})")]
    private static partial void LogCleanupFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "按键录入消息循环退出异常 (ExceptionType={ExceptionType})")]
    private static partial void LogMessageLoopError(ILogger logger, string exceptionType);
}
