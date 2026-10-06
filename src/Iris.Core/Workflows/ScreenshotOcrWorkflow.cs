using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

internal static partial class ScreenshotOcrWorkflow
{
    internal static async Task ExecuteAsync(ActionContext context, ITranslateWindowPresenter presenter,
        IScreenshotService screenshot, IOcrService ocr, ScreenshotPurpose purpose, bool autoCopy,
        ILogger logger, CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(context);
        caller.ThrowIfCancellationRequested();
        using var operation = presenter.TryBeginOperation(caller);
        if (operation == null) return;
        var token = operation.CancellationToken;
        ScreenshotResult? shot = null;
        ErrorCode? error = null;
        try
        {
            CheckCancellation(caller, token);
            shot = await screenshot.CaptureRegionAsync(purpose, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = ReportFailure(logger, ex, caller, token); }

        // Presentation failures belong to the Dispatcher boundary, not a second publication.
        if (error is { } captureError)
        {
            await presenter.TryNotifyAsync(operation, NotificationLevel.Error, captureError).ConfigureAwait(false);
            return;
        }
        if (shot == null) return;
        CheckCancellation(caller, token);
        var anchor = new PixelPoint(shot.Region.Right, shot.Region.Bottom);
        if (!await presenter.TryShowAsync(operation, new TranslateWindowRequest
        {
            Origin = TextOrigin.Screenshot, Anchor = anchor, AutoTranslate = false,
            StatusMessage = "正在识别文字…"
        }).ConfigureAwait(false)) return;

        OcrOutcome? outcome = null;
        try
        {
            CheckCancellation(caller, token);
            outcome = await ocr.RecognizeAsync(shot.Image, cancellationToken: token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(outcome.Text)) error = ErrorCode.OcrNoTextFound;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = ReportFailure(logger, ex, caller, token); }

        CheckCancellation(caller, token);
        await presenter.TryShowAsync(operation, new TranslateWindowRequest
        {
            Text = error == null ? outcome!.Text : null,
            Origin = TextOrigin.Screenshot, Anchor = anchor,
            AutoTranslate = error == null && purpose == ScreenshotPurpose.Translate,
            AutoCopySource = error == null && autoCopy,
            NotifyOcrFallback = error == null && outcome!.UsedFallback,
            Error = error is { } code ? new ErrorInfo(code, ErrorMessages.GetDefault(code)) : null
        }).ConfigureAwait(false);
    }

    internal static void CheckCancellation(CancellationToken caller, CancellationToken lifetime)
    {
        caller.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
    }

    internal static ErrorCode ReportFailure(ILogger logger, Exception exception,
        CancellationToken caller, CancellationToken lifetime)
    {
        var code = exception is IrisException known && Enum.IsDefined(known.Code) ? known.Code : ErrorCode.Unknown;
        LogFailure(logger, code, exception.GetType().Name);
        // A cleanup failure after cancellation is still a failure, never a canceled result.
        if (caller.IsCancellationRequested || lifetime.IsCancellationRequested)
            throw new IrisException(code, ErrorMessages.GetDefault(code));
        return code;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "截图OCR失败: ErrorCode={Code}, ExceptionType={ExceptionType}")]
    private static partial void LogFailure(ILogger logger, ErrorCode code, string exceptionType);
}
