using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Selection;

/// <summary>
/// Platform 内部 UIA 选区读取器父端。
/// 按需单例注册，依赖窄进程适配器、目标校验适配器与 TimeProvider。
/// 构造、DI 解析均不启动子进程或 COM。
/// 采用每请求一个隔离子进程，具备严格的时间预算、退出确认与安全错误映射。
/// </summary>
internal sealed class UiaSelectionReader : IAsyncDisposable, IDisposable
{
    private readonly IUiaProcessRunner _processRunner;
    private readonly ITargetWindowValidator _targetValidator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UiaSelectionReader>? _logger;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopCts = new();
    private bool _isStopped;
    private ReadOperationContext? _activeOperation;
    private SessionCleanupCoordinator? _failedCleanupCoordinator;
    private Task? _activeStopTask;

    public UiaSelectionReader(
        IUiaProcessRunner processRunner,
        ITargetWindowValidator targetValidator,
        TimeProvider timeProvider,
        ILogger<UiaSelectionReader>? logger = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _targetValidator = targetValidator ?? throw new ArgumentNullException(nameof(targetValidator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    public async Task<UiaSelectionResult> ReadAsync(nint sourceWindow, uint sourceProcessId, CancellationToken cancellationToken = default)
    {
        // 0. 调用线程纯参数同步校验
        if (sourceWindow == 0 || sourceProcessId == 0)
        {
            throw new IrisException(ErrorCode.InvalidArgument, "Source window and process ID must be non-zero.");
        }

        string requestId = Guid.NewGuid().ToString("N");
        ReadOperationContext operation;

        // 1. 原子准入：在任何 native/进程操作之前取得唯一活动槽
        lock (_gate)
        {
            if (_isStopped)
            {
                throw new InvalidOperationException("UiaSelectionReader is permanently stopped.");
            }

            if (_failedCleanupCoordinator != null)
            {
                throw new IrisException(ErrorCode.CaptureFailed, "Previous child process cleanup failed and requires retry before new reads.");
            }

            if (_activeOperation != null && !_activeOperation.ExecutionTask.IsCompleted)
            {
                return UiaSelectionResult.CreateBusy(requestId);
            }

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
            operation = new ReadOperationContext(requestId, sourceWindow, sourceProcessId, linkedCts, cancellationToken);
            _activeOperation = operation;
        }

        // 2. 全部目标校验、进程启动、I/O 与退出等待必须离开 UI 线程执行
        _ = Task.Run(() => ExecuteReadCoreAsync(operation), CancellationToken.None);

        return await operation.ExecutionTask.ConfigureAwait(false);
    }

    private async Task ExecuteReadCoreAsync(ReadOperationContext op)
    {
        try
        {
            // 1. 后台线程前置目标校验
            bool targetValid;
            try
            {
                targetValid = _targetValidator.IsTargetValidAndForeground(op.SourceWindow, op.SourceProcessId);
            }
            catch (Exception ex)
            {
                op.TrySetException(new IrisException(ErrorCode.CaptureFailed, "TARGET_VALIDATION_FAILED", ex));
                return;
            }

            if (!targetValid)
            {
                op.TrySetResult(UiaSelectionResult.CreateTargetChanged(op.RequestId));
                return;
            }

            if (op.LinkedCts.IsCancellationRequested || _isStopped)
            {
                op.CallerToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Reader was canceled or stopped during target validation.");
            }

            // 2. Ready 预算 1500ms：从本次实际接纳/启动阶段开始计时，包含启动和握手
            using var readyBudgetCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.ReadyTimeoutMs), _timeProvider);
            using var readyLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(op.LinkedCts.Token, readyBudgetCts.Token);

            Task<IUiaProcessSession> startTask;
            try
            {
                startTask = _processRunner.StartAsync(readyLinkedCts.Token);
                op.StartTask = startTask;
            }
            catch (FileNotFoundException)
            {
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "HOST_EXECUTABLE_NOT_FOUND"));
                return;
            }
            catch (Exception)
            {
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "PROCESS_START_FAILED"));
                return;
            }

            IUiaProcessSession? session = null;
            bool startTimedOut = false;
            bool startCanceled = false;

            try
            {
                session = await startTask.WaitAsync(readyLinkedCts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    op.Session = session;
                    op.Coordinator = new SessionCleanupCoordinator(session, op, _timeProvider);
                }
            }
            catch (OperationCanceledException) when (readyBudgetCts.IsCancellationRequested && !op.CallerToken.IsCancellationRequested && !_isStopped)
            {
                startTimedOut = true;
            }
            catch (OperationCanceledException)
            {
                startCanceled = true;
            }
            catch (FileNotFoundException)
            {
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "HOST_EXECUTABLE_NOT_FOUND"));
                return;
            }
            catch (Exception)
            {
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "PROCESS_START_FAILED"));
                return;
            }

            // 迟到启动收尾：若启动等待超时或取消，必须等待实际 startTask 完成并执行物理清理
            if (startTimedOut || startCanceled)
            {
                IUiaProcessSession? delayedSession = null;
                try
                {
                    delayedSession = await startTask.ConfigureAwait(false);
                }
                catch
                {
                    // 启动抛错，无物理会话产生
                }

                if (delayedSession != null)
                {
                    lock (_gate)
                    {
                        op.Session = delayedSession;
                        op.Coordinator = new SessionCleanupCoordinator(delayedSession, op, _timeProvider);
                    }
                    op.WasKilledDueToCancelOrStop = true;
                    await PerformCleanupAsync(op.Coordinator, normalExitGraceMs: 0).ConfigureAwait(false);
                }

                if (startCanceled)
                {
                    op.CallerToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException("Read operation was canceled during process start.");
                }

                op.TrySetResult(UiaSelectionResult.CreateTimedOut(op.RequestId));
                return;
            }

            // 3. 读取 Ready 帧（共享剩余的 1500ms 启动握手预算）
            string? readyLine;
            try
            {
                readyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                    session!.StandardOutput,
                    UiaProtocolConstants.MaxFrameBytes,
                    readyLinkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readyBudgetCts.IsCancellationRequested && !op.CallerToken.IsCancellationRequested && !_isStopped)
            {
                op.WasKilledDueToCancelOrStop = true;
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                op.TrySetResult(UiaSelectionResult.CreateTimedOut(op.RequestId));
                return;
            }
            catch (OperationCanceledException)
            {
                op.WasKilledDueToCancelOrStop = true;
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "READY_READ_FAILED"));
                return;
            }

            var ready = readyLine != null ? UiaProtocolCodec.DeserializeReady(readyLine) : null;
            if (ready == null || ready.ProtocolVersion != UiaProtocolConstants.CurrentVersion || ready.Kind != "Ready")
            {
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "INVALID_READY_FRAME"));
                return;
            }

            // 4. 发送 Request 帧并接收 Reply 帧（单调请求响应预算 300ms）
            using var requestBudgetCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.RequestTimeoutMs), _timeProvider);
            using var requestLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(op.LinkedCts.Token, requestBudgetCts.Token);

            var requestFrame = new UiaRequestFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                Kind = "Request",
                RequestId = op.RequestId,
                Hwnd = op.SourceWindow,
                ProcessId = op.SourceProcessId
            };

            string? replyLine;
            try
            {
                await UiaProtocolCodec.WriteFrameAsync(session.StandardInput, requestFrame, requestLinkedCts.Token).ConfigureAwait(false);

                replyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                    session.StandardOutput,
                    UiaProtocolConstants.MaxFrameBytes,
                    requestLinkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestBudgetCts.IsCancellationRequested && !op.CallerToken.IsCancellationRequested && !_isStopped)
            {
                op.WasKilledDueToCancelOrStop = true;
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                op.TrySetResult(UiaSelectionResult.CreateTimedOut(op.RequestId));
                return;
            }
            catch (OperationCanceledException)
            {
                op.WasKilledDueToCancelOrStop = true;
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "REPLY_READ_FAILED"));
                return;
            }

            var reply = replyLine != null ? UiaProtocolCodec.DeserializeReply(replyLine) : null;
            if (reply == null ||
                reply.ProtocolVersion != UiaProtocolConstants.CurrentVersion ||
                reply.Kind != "Reply" ||
                reply.RequestId != op.RequestId)
            {
                await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: 0).ConfigureAwait(false);
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "INVALID_REPLY_FRAME"));
                return;
            }

            // 5. 响应成功也必须等待宿主进程正常退出（宽限 1000ms），排空并校验尾部 EOF，物理收尾必须完整覆盖
            await PerformCleanupAsync(op.Coordinator!, normalExitGraceMs: UiaProtocolConstants.ExitGraceMs).ConfigureAwait(false);

            if (op.ExtraLineDetected || op.TailIoError)
            {
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "PROTOCOL_ERROR"));
                return;
            }

            // 6. 进程退出码与取消/停止消歧：若发生过取消/Stop，优先保留取消语义
            if (op.WasKilledDueToCancelOrStop || op.CallerToken.IsCancellationRequested || _isStopped)
            {
                op.CallerToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Reader was canceled or stopped during process exit wait.");
            }

            if (session.ExitCode != 0)
            {
                op.TrySetResult(UiaSelectionResult.CreateFailed(op.RequestId, "PROCESS_EXIT_UNEXPECTED"));
                return;
            }

            // 7. 结果发布前复核前台目标
            bool finalTargetValid;
            try
            {
                finalTargetValid = _targetValidator.IsTargetValidAndForeground(op.SourceWindow, op.SourceProcessId);
            }
            catch (Exception ex)
            {
                op.TrySetException(new IrisException(ErrorCode.CaptureFailed, "TARGET_VALIDATION_FAILED", ex));
                return;
            }

            if (!finalTargetValid)
            {
                op.TrySetResult(UiaSelectionResult.CreateTargetChanged(op.RequestId));
                return;
            }

            // 8. 目标校验返回之后、结果发布点，同时复核调用方取消、Stop 与操作上下文身份
            op.CallerToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_isStopped)
                {
                    throw new OperationCanceledException("Reader was stopped before result publication.");
                }
                if (_activeOperation != op)
                {
                    throw new OperationCanceledException("Operation context superseded before result publication.");
                }
            }

            // 9. 根据 Reply 状态发布安全结果
            var result = reply.Status switch
            {
                UiaSelectionStatus.Success => UiaSelectionResult.CreateSuccess(op.RequestId, reply.Text ?? string.Empty),
                UiaSelectionStatus.Empty => UiaSelectionResult.CreateEmpty(op.RequestId),
                UiaSelectionStatus.Unsupported => UiaSelectionResult.CreateUnsupported(op.RequestId, reply.ErrorCode),
                UiaSelectionStatus.Sensitive => UiaSelectionResult.CreateSensitive(op.RequestId),
                UiaSelectionStatus.TooLong => UiaSelectionResult.CreateTooLong(op.RequestId, reply.ErrorCode),
                UiaSelectionStatus.TargetChanged => UiaSelectionResult.CreateTargetChanged(op.RequestId),
                _ => UiaSelectionResult.CreateFailed(op.RequestId, reply.ErrorCode)
            };

            op.TrySetResult(result);
        }
        catch (OperationCanceledException oce)
        {
            op.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            op.TrySetException(ex);
        }
        finally
        {
            op.LinkedCts.Dispose();
            lock (_gate)
            {
                if (_activeOperation == op)
                {
                    _activeOperation = null;
                }
            }
        }
    }

    private async Task PerformCleanupAsync(SessionCleanupCoordinator coordinator, int normalExitGraceMs)
    {
        try
        {
            await coordinator.CleanupAsync(normalExitGraceMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _failedCleanupCoordinator = coordinator;
            }
            throw;
        }
    }

    public Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ExecuteRetryCleanupCoreAsync(cancellationToken), CancellationToken.None);
    }

    private async Task ExecuteRetryCleanupCoreAsync(CancellationToken cancellationToken)
    {
        SessionCleanupCoordinator? coordinator;
        lock (_gate)
        {
            coordinator = _failedCleanupCoordinator;
        }

        if (coordinator == null)
        {
            return;
        }

        await coordinator.CleanupAsync(normalExitGraceMs: 0).ConfigureAwait(false);

        lock (_gate)
        {
            if (_failedCleanupCoordinator == coordinator)
            {
                _failedCleanupCoordinator = null;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _isStopped = true;
            _stopCts.Cancel();

            if (_activeStopTask != null && !_activeStopTask.IsCompleted)
            {
                return _activeStopTask;
            }

            _activeStopTask = Task.Run(() => ExecuteStopCoreAsync(cancellationToken), CancellationToken.None);
            return _activeStopTask;
        }
    }

    private async Task ExecuteStopCoreAsync(CancellationToken cancellationToken)
    {
        ReadOperationContext? op;
        SessionCleanupCoordinator? failedCoordinator;
        lock (_gate)
        {
            op = _activeOperation;
            failedCoordinator = _failedCleanupCoordinator;
        }

        Exception? stopException = null;

        if (op != null)
        {
            if (op.Coordinator != null)
            {
                op.WasKilledDueToCancelOrStop = true;
                try
                {
                    await PerformCleanupAsync(op.Coordinator, normalExitGraceMs: 0).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    stopException = ex;
                }
            }

            try
            {
                await op.ExecutionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation
            }
            catch (Exception ex)
            {
                stopException ??= ex;
            }
        }

        if (failedCoordinator != null)
        {
            try
            {
                await PerformCleanupAsync(failedCoordinator, normalExitGraceMs: 0).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_failedCleanupCoordinator == failedCoordinator)
                    {
                        _failedCleanupCoordinator = null;
                    }
                }
            }
            catch (Exception ex)
            {
                stopException ??= ex;
            }
        }

        lock (_gate)
        {
            if (_failedCleanupCoordinator != null && stopException == null)
            {
                stopException = new IrisException(ErrorCode.CaptureFailed, "Child process cleanup failed.");
            }
        }

        if (stopException != null)
        {
            throw stopException;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_activeOperation != null && !_activeOperation.ExecutionTask.IsCompleted)
            {
                throw new InvalidOperationException("Cannot synchronously dispose UiaSelectionReader while a read operation is active.");
            }

            if (_failedCleanupCoordinator != null)
            {
                throw new InvalidOperationException("Cannot synchronously dispose UiaSelectionReader while a failed cleanup is pending.");
            }

            _isStopped = true;
            _stopCts.Cancel();
        }
    }

    private sealed class ReadOperationContext
    {
        public string RequestId { get; }
        public nint SourceWindow { get; }
        public uint SourceProcessId { get; }
        public CancellationTokenSource LinkedCts { get; }
        public CancellationToken CallerToken { get; }
        private readonly TaskCompletionSource<UiaSelectionResult> _tcs;
        public Task<UiaSelectionResult> ExecutionTask => _tcs.Task;

        public Task<IUiaProcessSession>? StartTask { get; set; }
        public IUiaProcessSession? Session { get; set; }
        public SessionCleanupCoordinator? Coordinator { get; set; }

        public bool ExtraLineDetected { get; set; }
        public bool TailIoError { get; set; }
        public bool WasKilledDueToCancelOrStop { get; set; }

        public ReadOperationContext(
            string requestId,
            nint sourceWindow,
            uint sourceProcessId,
            CancellationTokenSource linkedCts,
            CancellationToken callerToken)
        {
            RequestId = requestId;
            SourceWindow = sourceWindow;
            SourceProcessId = sourceProcessId;
            LinkedCts = linkedCts;
            CallerToken = callerToken;
            _tcs = new TaskCompletionSource<UiaSelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool TrySetResult(UiaSelectionResult result) => _tcs.TrySetResult(result);
        public bool TrySetException(Exception ex) => _tcs.TrySetException(ex);
        public bool TrySetCanceled(CancellationToken ct) => _tcs.TrySetCanceled(ct);
    }

    private sealed class SessionCleanupCoordinator
    {
        private readonly IUiaProcessSession _session;
        private readonly ReadOperationContext? _op;
        private readonly TimeProvider _timeProvider;
        private readonly object _cleanupGate = new();

        private Task? _activeCleanupTask;

        public IUiaProcessSession Session => _session;
        public bool ExitConfirmed { get; private set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsCompleted => ExitConfirmed && Disposed;

        public SessionCleanupCoordinator(IUiaProcessSession session, ReadOperationContext? op, TimeProvider timeProvider)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _op = op;
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public Task CleanupAsync(int normalExitGraceMs)
        {
            lock (_cleanupGate)
            {
                if (_activeCleanupTask != null && !_activeCleanupTask.IsCompleted)
                {
                    return _activeCleanupTask;
                }

                if (IsCompleted)
                {
                    return Task.CompletedTask;
                }

                _activeCleanupTask = ExecuteCleanupCoreAsync(normalExitGraceMs);
                return _activeCleanupTask;
            }
        }

        private async Task ExecuteCleanupCoreAsync(int normalExitGraceMs)
        {
            Exception? phaseFailure = null;
            string? failedPhase = null;

            // 阶段 1：宽限等待正常退出
            if (normalExitGraceMs > 0 && !ExitConfirmed)
            {
                bool hasExited = false;
                try
                {
                    hasExited = _session.HasExited;
                }
                catch (Exception ex)
                {
                    phaseFailure = ex;
                    failedPhase = "CHECK_EXIT";
                }

                if (!hasExited && phaseFailure == null)
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(normalExitGraceMs), _timeProvider);
                    using var graceCts = _op != null
                        ? CancellationTokenSource.CreateLinkedTokenSource(_op.LinkedCts.Token, timeoutCts.Token)
                        : null;
                    var tokenToUse = graceCts?.Token ?? timeoutCts.Token;

                    try
                    {
                        await _session.WaitForExitAsync(tokenToUse).ConfigureAwait(false);
                        ExitConfirmed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        // 宽限超时或请求取消，降级进入 Kill
                    }
                    catch (Exception ex)
                    {
                        phaseFailure = ex;
                        failedPhase = "WAIT_EXIT";
                    }
                }
                else if (hasExited)
                {
                    ExitConfirmed = true;
                }
            }
            else if (!ExitConfirmed)
            {
                try
                {
                    if (_session.HasExited)
                    {
                        ExitConfirmed = true;
                    }
                }
                catch (Exception ex)
                {
                    phaseFailure ??= ex;
                    failedPhase ??= "CHECK_EXIT";
                }
            }

            // 阶段 2 & 3：未确认退出时强制 Kill 并等待物理确认（预算 1000ms）
            if (!ExitConfirmed)
            {
                try
                {
                    _session.Kill();
                    Killed = true;
                }
                catch (Exception ex)
                {
                    phaseFailure ??= ex;
                    failedPhase ??= "KILL";
                }

                using var killCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.KillConfirmationMs), _timeProvider);
                try
                {
                    await _session.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                    ExitConfirmed = true;
                }
                catch (Exception ex)
                {
                    phaseFailure ??= ex;
                    failedPhase ??= "KILL_CONFIRM";
                }
            }

            // 阶段 4：尾部输出校验与排空
            if (_op != null && ExitConfirmed && phaseFailure == null && normalExitGraceMs > 0)
            {
                try
                {
                    using var eofDrainCts = new CancellationTokenSource(100);
                    string? extraLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                        _session.StandardOutput,
                        UiaProtocolConstants.MaxFrameBytes,
                        eofDrainCts.Token).ConfigureAwait(false);
                    if (extraLine != null)
                    {
                        _op.ExtraLineDetected = true;
                    }
                }
                catch (IOException ex) when (UiaProtocolFrameReader.IsPipeClosedException(ex))
                {
                    // 正常的平台管道关闭
                }
                catch (Exception ex)
                {
                    _op.TailIoError = true;
                    phaseFailure ??= ex;
                    failedPhase ??= "TAIL_READ";
                }
            }

            // 阶段 5：释放会话及流（仅在物理退出确认后释放）
            if (ExitConfirmed && !Disposed)
            {
                try
                {
                    await _session.DisposeAsync().ConfigureAwait(false);
                    Disposed = true;
                }
                catch (Exception ex)
                {
                    phaseFailure ??= ex;
                    failedPhase ??= "DISPOSE";
                }
            }

            // 校验终态
            if (!ExitConfirmed || !Disposed || phaseFailure != null)
            {
                int hr = phaseFailure?.HResult ?? -1;
                throw new IrisException(
                    ErrorCode.CaptureFailed,
                    $"CLEANUP_FAILED phase={failedPhase ?? "UNKNOWN"} hr=0x{hr:X8}");
            }
        }
    }
}
