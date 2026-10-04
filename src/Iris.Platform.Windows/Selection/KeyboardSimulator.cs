using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// 受控 Ctrl+C 键盘模拟器。
/// 仅供选区读取服务调用；构造、DI 解析零 native I/O。
/// 严格采用单 INPUT 确认、修饰键释放等待、目标身份复核及反向有界补偿机制。
/// </summary>
internal sealed partial class KeyboardSimulator : IDisposable, IAsyncDisposable
{
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_C = 0x43;

    private static readonly int[] WatchedVirtualKeys =
    [
        VK_LCONTROL,
        VK_RCONTROL,
        VK_LMENU,
        VK_RMENU,
        VK_LSHIFT,
        VK_RSHIFT,
        VK_LWIN,
        VK_RWIN,
        VK_C
    ];

    private readonly INativeKeyboardInputAdapter _nativeAdapter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KeyboardSimulator> _logger;

    private readonly object _syncRoot = new();
    private readonly HashSet<ushort> _pendingReleaseKeys = [];

    private volatile bool _isStopped;
    private int _disposed;

    private CancellationTokenSource? _activeWaitCts;
    private Task<KeyboardCopyResult>? _activeCopyTask;
    private Task? _activeCleanupTask;
    private Task? _activeStopTask;

    public KeyboardSimulator(
        INativeKeyboardInputAdapter? nativeAdapter = null,
        TimeProvider? timeProvider = null,
        ILogger<KeyboardSimulator>? logger = null)
    {
        _nativeAdapter = nativeAdapter ?? Win32KeyboardInputAdapter.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<KeyboardSimulator>.Instance;
    }

    /// <summary>
    /// 是否存在尚未释放的原生按键责任。
    /// </summary>
    public bool HasPendingRelease
    {
        get
        {
            lock (_syncRoot)
            {
                return _pendingReleaseKeys.Count > 0;
            }
        }
    }

    /// <summary>
    /// 执行一次受控 Ctrl+C 复制。
    /// 一次仅接纳一个活动调用；忙时立即返回 Busy。
    /// </summary>
    public Task<KeyboardCopyResult> CopyAsync(KeyboardCopyTarget target, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource waitCts;
        Task<KeyboardCopyResult> copyTask;

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, typeof(KeyboardSimulator));

            if (_isStopped)
            {
                throw new InvalidOperationException("The keyboard simulator has been stopped.");
            }

            if (target.WindowHandle == 0 || target.ProcessId == 0)
            {
                throw new IrisException(ErrorCode.InvalidArgument, "Target WindowHandle and ProcessId must be non-zero.");
            }

            if (_pendingReleaseKeys.Count > 0 ||
                (_activeCopyTask != null && !_activeCopyTask.IsCompleted) ||
                (_activeCleanupTask != null && !_activeCleanupTask.IsCompleted) ||
                (_activeStopTask != null && !_activeStopTask.IsCompleted))
            {
                return Task.FromResult(new KeyboardCopyResult(KeyboardCopyOutcome.Busy));
            }

            waitCts = new CancellationTokenSource();
            _activeWaitCts = waitCts;
            copyTask = Task.Run(
                () => RunCopyWorkflowAsync(target, waitCts, cancellationToken),
                CancellationToken.None);
            _activeCopyTask = copyTask;
        }

        return copyTask;
    }

    private async Task<KeyboardCopyResult> RunCopyWorkflowAsync(
        KeyboardCopyTarget target,
        CancellationTokenSource waitCts,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteCopyCoreAsync(target, cancellationToken, waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCopyExecutionError(_logger, ex.GetType().Name);
            throw;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeWaitCts, waitCts))
                {
                    _activeWaitCts = null;
                }
            }
            waitCts.Dispose();
        }
    }

    private async Task<KeyboardCopyResult> ExecuteCopyCoreAsync(
        KeyboardCopyTarget target,
        CancellationToken callerToken,
        CancellationToken stopToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, stopToken);
        var token = linkedCts.Token;

        token.ThrowIfCancellationRequested();

        // 1. 初始目标校验
        if (!IsTargetValidAndForeground(target))
        {
            return new KeyboardCopyResult(KeyboardCopyOutcome.TargetChanged, MayHaveCopied: false);
        }

        // 2. 修饰键释放等待轮询（10ms 间隔，最多 500ms）
        var timeout = TimeSpan.FromMilliseconds(500);
        var interval = TimeSpan.FromMilliseconds(10);
        long startTimestamp = _timeProvider.GetTimestamp();

        while (AreAnyWatchedKeysDown())
        {
            if (_timeProvider.GetElapsedTime(startTimestamp) >= timeout)
            {
                return new KeyboardCopyResult(KeyboardCopyOutcome.NotReady, MayHaveCopied: false);
            }

            await Task.Delay(interval, _timeProvider, token).ConfigureAwait(false);

            if (!IsTargetValidAndForeground(target))
            {
                return new KeyboardCopyResult(KeyboardCopyOutcome.TargetChanged, MayHaveCopied: false);
            }
        }

        // 3. 进入发送前的最终检查
        token.ThrowIfCancellationRequested();

        if (AreAnyWatchedKeysDown())
        {
            return new KeyboardCopyResult(KeyboardCopyOutcome.NotReady, MayHaveCopied: false);
        }

        if (!IsTargetValidAndForeground(target))
        {
            return new KeyboardCopyResult(KeyboardCopyOutcome.TargetChanged, MayHaveCopied: false);
        }

        // 发送前复核：最后一次原生按键与目标查询完成后，在开始第一个 SendInput 之前，重新确认本次取消与停止状态
        lock (_syncRoot)
        {
            if (_isStopped)
            {
                throw new OperationCanceledException(token);
            }
        }

        token.ThrowIfCancellationRequested();

        // 4. 发送阶段：不可被晚到取消打断，不插入 await/Delay
        return ExecuteSendSequence(target);
    }

    private KeyboardCopyResult ExecuteSendSequence(KeyboardCopyTarget target)
    {
        bool mayHaveCopied = false;

        // Step 1: LCtrl down
        var sendResult = TrySendInput(VK_LCONTROL, isKeyUp: false, out int err, out var ex);
        if (sendResult == SingleSendResult.Success)
        {
            TrackKeyDown(VK_LCONTROL);
        }
        else if (sendResult == SingleSendResult.Faulted)
        {
            TrackKeyDown(VK_LCONTROL);
            CompensatePendingKeys();
            return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(err, ex));
        }
        else
        {
            return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(err, null));
        }

        try
        {
            // Step 2: C down 前复核目标身份
            bool targetValid;
            try
            {
                targetValid = IsTargetValidAndForeground(target);
            }
            catch (Exception exTarget)
            {
                CompensatePendingKeys();
                return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(0, exTarget));
            }

            if (!targetValid)
            {
                bool compOk = CompensatePendingKeys();
                if (!compOk)
                {
                    return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(0, null));
                }
                return new KeyboardCopyResult(KeyboardCopyOutcome.TargetChanged, MayHaveCopied: mayHaveCopied);
            }

            sendResult = TrySendInput(VK_C, isKeyUp: false, out err, out ex);
            if (sendResult == SingleSendResult.Success)
            {
                mayHaveCopied = true;
                TrackKeyDown(VK_C);
            }
            else if (sendResult == SingleSendResult.Faulted)
            {
                mayHaveCopied = true;
                TrackKeyDown(VK_C);
                CompensatePendingKeys();
                return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(err, ex));
            }
            else
            {
                CompensatePendingKeys();
                return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(err, null));
            }

            // Step 3: C up
            sendResult = TrySendInput(VK_C, isKeyUp: true, out err, out ex);
            if (sendResult == SingleSendResult.Success)
            {
                TrackKeyUp(VK_C);
            }
            else
            {
                CompensatePendingKeys();
                return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(err, ex));
            }

            // Step 4: LCtrl up
            sendResult = TrySendInput(VK_LCONTROL, isKeyUp: true, out err, out ex);
            if (sendResult == SingleSendResult.Success)
            {
                TrackKeyUp(VK_LCONTROL);
                return new KeyboardCopyResult(KeyboardCopyOutcome.Injected, MayHaveCopied: mayHaveCopied, Error: null);
            }
            else
            {
                CompensatePendingKeys();
                return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(err, ex));
            }
        }
        catch (Exception exSend)
        {
            CompensatePendingKeys();
            return new KeyboardCopyResult(KeyboardCopyOutcome.Failed, MayHaveCopied: mayHaveCopied, CreateErrorInfo(0, exSend));
        }
    }

    /// <summary>
    /// 重试清理尚待释放的原生按键。
    /// 无待释放按键时幂等且零 native 调用。
    /// 多个并发调用共享已登记的清理活动，防止重复发送 KeyUp。
    /// </summary>
    public Task RetryCleanupAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(KeyboardSimulator));

        lock (_syncRoot)
        {
            if (_pendingReleaseKeys.Count == 0)
            {
                return Task.CompletedTask;
            }

            if (_activeCleanupTask != null && !_activeCleanupTask.IsCompleted)
            {
                return _activeCleanupTask;
            }

            _activeCleanupTask = Task.Run(() => RunCleanupWorkflowAsync(), CancellationToken.None);
            return _activeCleanupTask;
        }
    }

    private async Task RunCleanupWorkflowAsync()
    {
        Task? copyToWait;
        lock (_syncRoot)
        {
            copyToWait = _activeCopyTask;
        }

        if (copyToWait != null)
        {
            try
            {
                await copyToWait.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await ExecuteCleanupCoreAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 永久停止模拟器，拒绝后续复制请求，取消等待并收尾残留按键。
    /// 失败时保留责任并允许再次调用重试。
    /// </summary>
    public Task StopAsync()
    {
        lock (_syncRoot)
        {
            _isStopped = true;
            _activeWaitCts?.Cancel();

            if (_activeStopTask != null && !_activeStopTask.IsCompleted)
            {
                return _activeStopTask;
            }

            if (_activeStopTask != null && _activeStopTask.IsCompletedSuccessfully && _pendingReleaseKeys.Count == 0)
            {
                return _activeStopTask;
            }

            _activeStopTask = Task.Run(() => RunStopWorkflowAsync(), CancellationToken.None);
            return _activeStopTask;
        }
    }

    private async Task RunStopWorkflowAsync()
    {
        Task? copyToWait;
        lock (_syncRoot)
        {
            copyToWait = _activeCopyTask;
        }

        if (copyToWait != null)
        {
            try
            {
                await copyToWait.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        Task? cleanupTask;
        lock (_syncRoot)
        {
            if (_pendingReleaseKeys.Count == 0)
            {
                return;
            }

            if (_activeCleanupTask == null || _activeCleanupTask.IsCompleted)
            {
                _activeCleanupTask = ExecuteCleanupCoreAsync();
            }
            cleanupTask = _activeCleanupTask;
        }

        if (cleanupTask != null)
        {
            await cleanupTask.ConfigureAwait(false);
        }
    }

    private async Task ExecuteCleanupCoreAsync()
    {
        bool success = await Task.Run(() =>
        {
            return CompensatePendingKeys();
        }).ConfigureAwait(false);

        if (!success)
        {
            throw new IrisException(ErrorCode.Unknown, "键盘按键释放清理失败，部分按键未能成功释放。");
        }
    }

    private bool CompensatePendingKeys()
    {
        // 严格按反向顺序：VK_C 然后 VK_LCONTROL
        ushort[] keysToRelease = [VK_C, VK_LCONTROL];
        bool allSucceeded = true;

        foreach (var vk in keysToRelease)
        {
            bool isPending;
            lock (_syncRoot)
            {
                isPending = _pendingReleaseKeys.Contains(vk);
            }

            if (isPending)
            {
                var res = TrySendInput(vk, isKeyUp: true, out _, out _);
                if (res == SingleSendResult.Success)
                {
                    TrackKeyUp(vk);
                }
                else
                {
                    allSucceeded = false;
                }
            }
        }

        return allSucceeded;
    }

    private void TrackKeyDown(ushort vk)
    {
        lock (_syncRoot)
        {
            _pendingReleaseKeys.Add(vk);
        }
    }

    private void TrackKeyUp(ushort vk)
    {
        lock (_syncRoot)
        {
            _pendingReleaseKeys.Remove(vk);
        }
    }

    private SingleSendResult TrySendInput(ushort vk, bool isKeyUp, out int nativeError, out Exception? exception)
    {
        nativeError = 0;
        exception = null;
        try
        {
            var input = CreateKeyboardInput(vk, isKeyUp);
            INPUT[] inputs = [input];
            int cbSize = Marshal.SizeOf<INPUT>();
            uint result = _nativeAdapter.SendInput(1, inputs, cbSize);
            if (result == 1)
            {
                return SingleSendResult.Success;
            }
            else if (result == 0)
            {
                nativeError = _nativeAdapter.GetLastError();
                return SingleSendResult.Rejected;
            }
            else
            {
                nativeError = _nativeAdapter.GetLastError();
                return SingleSendResult.Faulted;
            }
        }
        catch (Exception ex)
        {
            exception = ex;
            return SingleSendResult.Faulted;
        }
    }

    internal static INPUT CreateKeyboardInput(ushort virtualKey, bool isKeyUp)
    {
        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)virtualKey,
                    wScan = 0,
                    dwFlags = isKeyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = 0
                }
            }
        };
    }

    private bool AreAnyWatchedKeysDown()
    {
        foreach (var vk in WatchedVirtualKeys)
        {
            if ((_nativeAdapter.GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsTargetValidAndForeground(KeyboardCopyTarget target)
    {
        if (target.WindowHandle == 0 || target.ProcessId == 0)
        {
            return false;
        }

        if (!_nativeAdapter.IsWindow(target.WindowHandle))
        {
            return false;
        }

        uint pid = _nativeAdapter.GetWindowProcessId(target.WindowHandle);
        if (pid != target.ProcessId)
        {
            return false;
        }

        uint currentPid = _nativeAdapter.GetCurrentProcessId();
        if (pid == currentPid)
        {
            return false;
        }

        nint foregroundHwnd = _nativeAdapter.GetForegroundWindow();
        if (foregroundHwnd != target.WindowHandle)
        {
            return false;
        }

        uint foregroundPid = _nativeAdapter.GetWindowProcessId(foregroundHwnd);
        if (foregroundPid != target.ProcessId || foregroundPid == currentPid)
        {
            return false;
        }

        return true;
    }

    private static ErrorInfo CreateErrorInfo(int nativeError, Exception? ex)
    {
        string detail = nativeError != 0
            ? $"SendInput Win32 error: {nativeError}"
            : ex != null
                ? $"SendInput exception: {ex.GetType().Name}"
                : "SendInput rejected input event.";
        return new ErrorInfo(
            Code: ErrorCode.Unknown,
            Message: "键盘模拟输入失败",
            Detail: detail);
    }

    private enum SingleSendResult
    {
        Success,
        Rejected,
        Faulted
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unexpected error during KeyboardSimulator.CopyAsync execution: {ExceptionType}")]
    private static partial void LogCopyExecutionError(ILogger logger, string exceptionType);

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed != 0)
            {
                return;
            }

            bool hasActiveOperation = (_activeCopyTask != null && !_activeCopyTask.IsCompleted)
                                   || (_activeCleanupTask != null && !_activeCleanupTask.IsCompleted)
                                   || (_activeStopTask != null && !_activeStopTask.IsCompleted);
            bool hasPending = _pendingReleaseKeys.Count > 0;

            if (hasActiveOperation || hasPending)
            {
                throw new InvalidOperationException(
                    "KeyboardSimulator cannot be synchronously disposed while operations are active or key releases are pending. Use StopAsync or DisposeAsync.");
            }

            _isStopped = true;
            _disposed = 1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_syncRoot)
        {
            if (_pendingReleaseKeys.Count > 0)
            {
                throw new InvalidOperationException("KeyboardSimulator cannot be disposed with pending key releases.");
            }
            _disposed = 1;
        }
    }
}
