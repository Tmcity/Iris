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
    private bool _isStopped;
    private Task<UiaSelectionResult>? _activeReadTask;
    private IUiaProcessSession? _activeSession;
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
        if (sourceWindow == 0 || sourceProcessId == 0)
        {
            throw new IrisException(ErrorCode.InvalidArgument, "Source window and process ID must be non-zero.");
        }

        string requestId = Guid.NewGuid().ToString("N");

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

            if (_activeReadTask != null && !_activeReadTask.IsCompleted)
            {
                return UiaSelectionResult.CreateBusy(requestId);
            }
        }

        // 进程启动前复核前台目标
        if (!_targetValidator.IsTargetValidAndForeground(sourceWindow, sourceProcessId))
        {
            return UiaSelectionResult.CreateTargetChanged(requestId);
        }

        Task<UiaSelectionResult> executionTask;
        lock (_gate)
        {
            if (_isStopped)
            {
                throw new InvalidOperationException("UiaSelectionReader is permanently stopped.");
            }

            executionTask = ExecuteReadCoreAsync(requestId, sourceWindow, sourceProcessId, cancellationToken);
            _activeReadTask = executionTask;
        }

        return await executionTask.ConfigureAwait(false);
    }

    private async Task<UiaSelectionResult> ExecuteReadCoreAsync(
        string requestId,
        nint sourceWindow,
        uint sourceProcessId,
        CancellationToken cancellationToken)
    {
        IUiaProcessSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. 启动子进程
            try
            {
                session = await _processRunner.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_isStopped)
                    {
                        throw new OperationCanceledException("Reader was stopped during process startup.");
                    }
                    _activeSession = session;
                }
            }
            catch (FileNotFoundException)
            {
                return UiaSelectionResult.CreateFailed(requestId, "HOST_EXECUTABLE_NOT_FOUND");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (session != null)
                {
                    await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception)
            {
                if (session != null)
                {
                    await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                }
                return UiaSelectionResult.CreateFailed(requestId, "PROCESS_START_FAILED");
            }

            // 2. 接收 Ready 帧 (预算 1500ms)
            using var readyTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.ReadyTimeoutMs), _timeProvider);
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readyTimeoutCts.Token);

            string? readyLine;
            try
            {
                readyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                    session.StandardOutput,
                    UiaProtocolConstants.MaxFrameBytes,
                    readyCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 启动 / Ready 超时
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                return UiaSelectionResult.CreateTimedOut(requestId);
            }
            catch (OperationCanceledException)
            {
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(requestId, "READY_READ_FAILED");
            }

            var ready = readyLine != null ? UiaProtocolCodec.DeserializeReady(readyLine) : null;
            if (ready == null || ready.ProtocolVersion != UiaProtocolConstants.CurrentVersion || ready.Kind != "Ready")
            {
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(requestId, "INVALID_READY_FRAME");
            }

            // 3. 发送 Request 帧并接收 Reply 帧 (单调请求响应预算 300ms)
            using var requestTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.RequestTimeoutMs), _timeProvider);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestTimeoutCts.Token);

            var requestFrame = new UiaRequestFrame
            {
                ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                Kind = "Request",
                RequestId = requestId,
                Hwnd = sourceWindow,
                ProcessId = sourceProcessId
            };

            string? replyLine;
            try
            {
                await UiaProtocolCodec.WriteFrameAsync(session.StandardInput, requestFrame, requestCts.Token).ConfigureAwait(false);

                replyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
                    session.StandardOutput,
                    UiaProtocolConstants.MaxFrameBytes,
                    requestCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // UIA 请求 300ms 超时
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                return UiaSelectionResult.CreateTimedOut(requestId);
            }
            catch (OperationCanceledException)
            {
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(requestId, "REPLY_READ_FAILED");
            }

            var reply = replyLine != null ? UiaProtocolCodec.DeserializeReply(replyLine) : null;
            if (reply == null ||
                reply.ProtocolVersion != UiaProtocolConstants.CurrentVersion ||
                reply.Kind != "Reply" ||
                reply.RequestId != requestId)
            {
                await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: 0).ConfigureAwait(false);
                return UiaSelectionResult.CreateFailed(requestId, "INVALID_REPLY_FRAME");
            }

            // 4. 响应成功也必须等待宿主进程退出 (宽限 1000ms)，未退出则终止并确认
            await CleanupSessionAsync(session, CancellationToken.None, normalExitGraceMs: UiaProtocolConstants.ExitGraceMs).ConfigureAwait(false);

            // 5. 结果发布前复核前台目标
            if (!_targetValidator.IsTargetValidAndForeground(sourceWindow, sourceProcessId))
            {
                return UiaSelectionResult.CreateTargetChanged(requestId);
            }

            // 6. 根据 Reply 状态构建最终安全结果
            return reply.Status switch
            {
                UiaSelectionStatus.Success => UiaSelectionResult.CreateSuccess(requestId, reply.Text ?? string.Empty),
                UiaSelectionStatus.Empty => UiaSelectionResult.CreateEmpty(requestId),
                UiaSelectionStatus.Unsupported => UiaSelectionResult.CreateUnsupported(requestId, reply.ErrorCode),
                UiaSelectionStatus.Sensitive => UiaSelectionResult.CreateSensitive(requestId),
                UiaSelectionStatus.TooLong => UiaSelectionResult.CreateTooLong(requestId, reply.ErrorCode),
                UiaSelectionStatus.TargetChanged => UiaSelectionResult.CreateTargetChanged(requestId),
                _ => UiaSelectionResult.CreateFailed(requestId, reply.ErrorCode)
            };
        }
        finally
        {
            lock (_gate)
            {
                if (_activeSession == session)
                {
                    _activeSession = null;
                }
                _activeReadTask = null;
            }
        }
    }

    private async Task CleanupSessionAsync(IUiaProcessSession session, CancellationToken ct, int normalExitGraceMs = 0)
    {
        bool exited = false;

        // 1. 若配置了正常退出宽限时间，先等待正常退出
        if (normalExitGraceMs > 0 && !session.HasExited)
        {
            using var graceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(normalExitGraceMs), _timeProvider);
            try
            {
                await session.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                exited = false;
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

        // 2. 未能正常退出时，终止子进程并等待物理退出确认 (预算 1000ms)
        if (!exited && !session.HasExited)
        {
            session.Kill();

            using var killCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(UiaProtocolConstants.KillConfirmationMs), _timeProvider);
            try
            {
                await session.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }
            catch
            {
                exited = false;
            }
        }
        else
        {
            exited = true;
        }

        // 3. 若仍无法确认退出，保留清理责任
        if (!exited && !session.HasExited)
        {
            lock (_gate)
            {
                _failedCleanupSession = session;
            }
            throw new IrisException(ErrorCode.CaptureFailed, "Failed to confirm physical exit of Iris.UiaHost process within confirmation budget.");
        }

        await session.DisposeAsync().ConfigureAwait(false);
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

        await CleanupSessionAsync(session, cancellationToken, normalExitGraceMs: 0).ConfigureAwait(false);
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
        // 观察在途读取任务
        Task? readTask;
        IUiaProcessSession? session;
        IUiaProcessSession? failedSession;
        lock (_gate)
        {
            readTask = _activeReadTask;
            session = _activeSession;
            failedSession = _failedCleanupSession;
        }

        if (session != null)
        {
            try
            {
                session.Kill();
            }
            catch
            {
                // ignore
            }
        }

        if (readTask != null)
        {
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore in-flight read task exception during stop
            }
        }

        if (session != null)
        {
            await CleanupSessionAsync(session, cancellationToken, normalExitGraceMs: 0).ConfigureAwait(false);
        }

        if (failedSession != null)
        {
            await CleanupSessionAsync(failedSession, cancellationToken, normalExitGraceMs: 0).ConfigureAwait(false);
            lock (_gate)
            {
                _failedCleanupSession = null;
            }
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
            _isStopped = true;

            if (_activeSession != null && _activeSession.HasExited)
            {
                _activeSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _activeSession = null;
            }

            if (_failedCleanupSession != null && _failedCleanupSession.HasExited)
            {
                _failedCleanupSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _failedCleanupSession = null;
            }
        }
    }
}
