using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Microsoft.Extensions.Logging;

namespace Iris.App.Services;

internal sealed partial class ScreenshotOcrFeedback(
    IUiDispatcher dispatcher, TrayNotificationService notifications,
    ILogger<ScreenshotOcrFeedback> logger) : IScreenshotOcrFeedback, IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stoppingCts = new();
    private bool _stopping;
    private int _inFlight;
    private Task? _cancelTask;
    private TaskCompletionSource? _drained;

    public CancellationToken StoppingToken => _stoppingCts.Token;

    public Task<bool> TryNotifyCopiedAsync(int characterCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characterCount);
        return NotifyAsync(() => notifications.TryNotifyOcrCopied(characterCount), cancellationToken);
    }

    public Task<bool> TryNotifyErrorAsync(ErrorCode code, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        return NotifyAsync(() => notifications.TryNotifyNow(NotificationLevel.Error, code), cancellationToken);
    }

    private async Task<bool> NotifyAsync(Func<bool> notify, CancellationToken caller)
    {
        lock (_gate)
        {
            if (_stopping || caller.IsCancellationRequested) return false;
            _inFlight++;
        }
        bool accepted = false;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (!_stopping && !caller.IsCancellationRequested) accepted = notify();
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            return accepted;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
                if (_stopping && _inFlight == 0) _drained?.TrySetResult();
            }
        }
    }

    internal void StopAcceptance()
    {
        lock (_gate)
        {
            _stopping = true;
            // CancelAsync marks the token synchronously, and runs callbacks asynchronously.
            _cancelTask ??= _stoppingCts.CancelAsync();
            _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight == 0) _drained.TrySetResult();
        }
    }

    internal async Task StopAsync()
    {
        StopAcceptance();
        Task cancel;
        Task drained;
        lock (_gate) { cancel = _cancelTask!; drained = _drained!.Task; }
        try { await cancel.ConfigureAwait(false); }
        catch (Exception ex)
        {
            LogCancelFailed(logger, ex.GetType().Name);
            // Callbacks have exited even if one threw. Retrying stop must not run them twice.
            lock (_gate) _cancelTask = Task.CompletedTask;
            await drained.ConfigureAwait(false);
            throw new IrisException(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
        }
        await drained.ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "静默OCR停止回调失败：{ExceptionType}")]
    private static partial void LogCancelFailed(ILogger logger, string exceptionType);

    public void Dispose()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (!_cancelTask!.IsCompletedSuccessfully || _inFlight != 0)
                throw new InvalidOperationException("静默OCR反馈尚未排空，请先完成异步停止。");
            _stoppingCts.Dispose();
        }
    }
}
