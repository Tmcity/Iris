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
    private IUiaProcessSession? _failedCleanupSession;
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

            if (_failedCleanupSession != null)
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
        var executionTask = Task.Run(() => ExecuteReadCoreAsync(operation), CancellationToken.None);
        operation.SetExecutionTask(executionTask);

        return await executionTask.ConfigureAwait(false);
    }

    private async Task<UiaSelectionResult> ExecuteReadCoreAsync(ReadOperationContext op)
    {
        try
        {
            // 1. 后台线程前置目标校验
            if (!_targetValidator.IsTargetValidAndForeground(op.SourceWindow, op.SourceProcessId))
            {
                return UiaSelectionResult.CreateTargetChanged(op.RequestId);
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
            }
            catch (FileNotFoundException)
            {
                return UiaSelectionResult.CreateFailed(op.RequestId, "HOST_EXECUTABLE_NOT_FOUND");
            }
            catch (Exception)
            {
                return UiaSelectionResult.CreateFailed(op.RequestId, "PROCESS_START_FAILED");
            }

            IUiaProcessSession? session = null;
            try
            {
                session = await startTask.WaitAsync(readyLinkedCts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    op.Session = session;
                }
            }
            catch (OperationCanceledException) when (readyBudgetCts.IsCancellationRequested && !op.CallerToken.IsCancellationRequested && !_isStopped)
            {
                _ = CleanupDelayedStartAsync(startTask, op);
                return UiaSelectionResult.CreateTimedOut(op.RequestId);
            }
            catch (OperationCanceledException)
            {
                _ = CleanupDelayedStartAsync(startTask, op);
                throw;
            }
            catch (FileNotFoundException)
            {
                return UiaSelectionResult.CreateFailed(op.RequestId, "HOST_EXECUTABLE_NOT_FOUND");
            }
            catch (Exception)
            {
                return UiaSelectionResult.CreateFailed(op.RequestId, "PROCESS_START_FAILED");
            }

            // 3. 读取 Ready 帧（共享剩余的 1500ms 启动握手预算）
            string? readyLine;
            try
            {
                readyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                    session.StandardOutput,
                    UiaProtocolConstants.MaxFrameBytes,
                    readyLinkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readyBudgetCts.IsCancellationRequested && !op.CallerToken.IsCancellationRequested && !_isStopped)
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                return UiaSelectionResult.CreateTimedOut(op.RequestId);
            }
            catch (OperationCanceledException)
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(op.RequestId, "READY_READ_FAILED");
            }

            var ready = readyLine != null ? UiaProtocolCodec.DeserializeReady(readyLine) : null;
            if (ready == null || ready.ProtocolVersion != UiaProtocolConstants.CurrentVersion || ready.Kind != "Ready")
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(op.RequestId, "INVALID_READY_FRAME");
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
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                return UiaSelectionResult.CreateTimedOut(op.RequestId);
            }
            catch (OperationCanceledException)
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(op.RequestId, "REPLY_READ_FAILED");
            }

            var reply = replyLine != null ? UiaProtocolCodec.DeserializeReply(replyLine) : null;
            if (reply == null ||
                reply.ProtocolVersion != UiaProtocolConstants.CurrentVersion ||
                reply.Kind != "Reply" ||
                reply.RequestId != op.RequestId)
            {
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(op.RequestId, "INVALID_REPLY_FRAME");
            }

            // 5. 响应成功也必须等待宿主进程正常退出（宽限 1000ms），排空并校验尾部 EOF，物理收尾必须完整覆盖
            await PerformCleanupAsync(session, normalExitGraceMs: UiaProtocolConstants.ExitGraceMs, op).ConfigureAwait(false);

            if (op.ExtraLineDetected)
            {
                return UiaSelectionResult.CreateFailed(op.RequestId, "PROTOCOL_ERROR");
            }

            if (session.ExitCode != 0)
            {
                return UiaSelectionResult.CreateFailed(op.RequestId, "PROCESS_EXIT_UNEXPECTED");
            }

            // 7. 响应已到但进程退出等待期间发生 Cancel/Stop 门闩
            op.CallerToken.ThrowIfCancellationRequested();
            if (_isStopped)
            {
                throw new OperationCanceledException("Reader was stopped before result publication.");
            }

            // 8. 结果发布前复核前台目标
            if (!_targetValidator.IsTargetValidAndForeground(op.SourceWindow, op.SourceProcessId))
            {
                return UiaSelectionResult.CreateTargetChanged(op.RequestId);
            }

            lock (_gate)
            {
                if (_isStopped)
                {
                    throw new OperationCanceledException("Reader was stopped before result publication.");
                }
            }

            // 9. 根据 Reply 状态发布安全结果
            return reply.Status switch
            {
                UiaSelectionStatus.Success => UiaSelectionResult.CreateSuccess(op.RequestId, reply.Text ?? string.Empty),
                UiaSelectionStatus.Empty => UiaSelectionResult.CreateEmpty(op.RequestId),
                UiaSelectionStatus.Unsupported => UiaSelectionResult.CreateUnsupported(op.RequestId, reply.ErrorCode),
                UiaSelectionStatus.Sensitive => UiaSelectionResult.CreateSensitive(op.RequestId),
                UiaSelectionStatus.TooLong => UiaSelectionResult.CreateTooLong(op.RequestId, reply.ErrorCode),
                UiaSelectionStatus.TargetChanged => UiaSelectionResult.CreateTargetChanged(op.RequestId),
                _ => UiaSelectionResult.CreateFailed(op.RequestId, reply.ErrorCode)
            };
        }
        finally
        {
            lock (_gate)
            {
                if (_activeOperation == op)
                {
                    _activeOperation = null;
                }
            }
        }
    }

    private Task PerformCleanupAsync(IUiaProcessSession session, int normalExitGraceMs, ReadOperationContext? op)
    {
        lock (_gate)
        {
            if (op != null)
            {
                if (op.CleanupTask != null)
                {
                    return op.CleanupTask;
                }
                op.CleanupTask = DoCleanupSessionCoreAsync(session, normalExitGraceMs, op);
                return op.CleanupTask;
            }
        }

        return DoCleanupSessionCoreAsync(session, normalExitGraceMs, op);
    }

    private async Task DoCleanupSessionCoreAsync(IUiaProcessSession session, int normalExitGraceMs, ReadOperationContext? op = null)
    {
        bool exited = false;
        Exception? cleanupFailure = null;

        // 阶段 1：宽限等待正常退出
        if (normalExitGraceMs > 0 && !session.HasExited)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(normalExitGraceMs), _timeProvider);
            using var graceCts = op != null
                ? CancellationTokenSource.CreateLinkedTokenSource(op.LinkedCts.Token, timeoutCts.Token)
                : null;
            var tokenToUse = graceCts?.Token ?? timeoutCts.Token;

            try
            {
                await session.WaitForExitAsync(tokenToUse).ConfigureAwait(false);
                exited = true;
            }
            catch
            {
                exited = false;
            }
        }
        else
        {
            exited = session.HasExited;
        }

        // 阶段 2：未正常退出时强制 Kill 并等待物理确认（预算 1000ms）
        if (!exited && !session.HasExited)
        {
            try
            {
                session.Kill();
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            using var killCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.KillConfirmationMs), _timeProvider);
            try
            {
                await session.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (Exception ex)
            {
                exited = false;
                cleanupFailure ??= ex;
            }
        }
        else
        {
            exited = true;
        }

        // 阶段 3：若仍未退出，保留清理责任，禁止后续 Read
        if (!exited && !session.HasExited)
        {
            lock (_gate)
            {
                _failedCleanupSession = session;
            }
            throw new IrisException(ErrorCode.CaptureFailed, "Failed to confirm physical exit of Iris.UiaHost process within confirmation budget.", cleanupFailure);
        }

        // 阶段 3.5：尾部输出校验与排空（仅在正常退出且未发生清理失败时，防止额外 Reply/坏帧）
        if (op != null && exited && cleanupFailure == null)
        {
            try
            {
                using var eofDrainCts = new CancellationTokenSource(100);
                string? extraLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                    session.StandardOutput,
                    UiaProtocolConstants.MaxFrameBytes,
                    eofDrainCts.Token).ConfigureAwait(false);
                if (extraLine != null)
                {
                    op.ExtraLineDetected = true;
                }
            }
            catch
            {
                op.ExtraLineDetected = true;
            }
        }

        // 阶段 4：处置会话及流
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _failedCleanupSession = session;
            }
            throw new IrisException(ErrorCode.CaptureFailed, "Failed to dispose Iris.UiaHost process session.", ex);
        }
    }

    private async Task CleanupDelayedStartAsync(Task<IUiaProcessSession> startTask, ReadOperationContext op)
    {
        try
        {
            var session = await startTask.ConfigureAwait(false);
            if (session != null)
            {
                lock (_gate)
                {
                    op.Session = session;
                }
                await PerformCleanupAsync(session, normalExitGraceMs: 0, op).ConfigureAwait(false);
            }
        }
        catch
        {
            // 启动失败，无需收尾
        }
    }

    public async Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        IUiaProcessSession? session;
        lock (_gate)
        {
            session = _failedCleanupSession;
        }

        if (session == null)
        {
            return;
        }

        await DoCleanupSessionCoreAsync(session, normalExitGraceMs: 0).ConfigureAwait(false);

        lock (_gate)
        {
            if (_failedCleanupSession == session)
            {
                _failedCleanupSession = null;
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

            _activeStopTask = ExecuteStopCoreAsync(cancellationToken);
            return _activeStopTask;
        }
    }

    private async Task ExecuteStopCoreAsync(CancellationToken cancellationToken)
    {
        ReadOperationContext? op;
        IUiaProcessSession? failedSession;
        lock (_gate)
        {
            op = _activeOperation;
            failedSession = _failedCleanupSession;
        }

        Exception? stopException = null;

        if (op != null)
        {
            if (op.Session != null)
            {
                try
                {
                    await PerformCleanupAsync(op.Session, normalExitGraceMs: 0, op).ConfigureAwait(false);
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

        if (failedSession != null)
        {
            try
            {
                await DoCleanupSessionCoreAsync(failedSession, normalExitGraceMs: 0).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_failedCleanupSession == failedSession)
                    {
                        _failedCleanupSession = null;
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
            if (_failedCleanupSession != null && stopException == null)
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

            if (_failedCleanupSession != null)
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
        public Task<UiaSelectionResult> ExecutionTask { get; private set; } = null!;
        public IUiaProcessSession? Session { get; set; }
        public Task? CleanupTask { get; set; }
        public bool ExtraLineDetected { get; set; }

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
        }

        public void SetExecutionTask(Task<UiaSelectionResult> task) => ExecutionTask = task;
    }
}
