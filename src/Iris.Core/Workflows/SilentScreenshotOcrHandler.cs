using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

internal sealed class SilentScreenshotOcrHandler(
    IScreenshotService screenshot, IOcrService ocr, IClipboardService clipboard,
    ISettingsService settings, IScreenshotOcrFeedback feedback,
    ILogger<SilentScreenshotOcrHandler> logger) : IActionHandler
{
    public IrisAction Action => IrisAction.SilentScreenshotOcr;

    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopping = feedback.StoppingToken;
        ScreenshotOcrWorkflow.CheckCancellation(cancellationToken, stopping);
        bool notify = settings.Current.Ocr.SilentOcrShowNotification;
        await using var linked = new ScreenshotOcrCancellation(logger, cancellationToken, stopping);
        ErrorCode? error = null;
        int count = 0;
        try
        {
            ScreenshotOcrWorkflow.CheckCancellation(cancellationToken, stopping);
            var shot = await screenshot.CaptureRegionAsync(ScreenshotPurpose.SilentOcr, linked.Token).ConfigureAwait(false);
            if (shot == null) return;
            ScreenshotOcrWorkflow.CheckCancellation(cancellationToken, stopping);
            var outcome = await ocr.RecognizeAsync(shot.Image, cancellationToken: linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(outcome.Text)) error = ErrorCode.OcrNoTextFound;
            else
            {
                ScreenshotOcrWorkflow.CheckCancellation(cancellationToken, stopping);
                await clipboard.SetTextAsync(outcome.Text, linked.Token).ConfigureAwait(false);
                count = outcome.Text.Length;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = ScreenshotOcrWorkflow.ReportFailure(logger, ex, cancellationToken, stopping); }

        ScreenshotOcrWorkflow.CheckCancellation(cancellationToken, stopping);
        if (error is { } code)
            await feedback.TryNotifyErrorAsync(code, cancellationToken).ConfigureAwait(false);
        else if (notify)
            await feedback.TryNotifyCopiedAsync(count, cancellationToken).ConfigureAwait(false);
    }
}
