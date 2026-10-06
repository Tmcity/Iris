using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

internal sealed class ScreenshotOcrHandler(
    ITranslateWindowPresenter presenter, IScreenshotService screenshot, IOcrService ocr,
    ISettingsService settings, ILogger<ScreenshotOcrHandler> logger) : IActionHandler
{
    public IrisAction Action => IrisAction.ScreenshotOcr;

    public Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        bool autoCopy = settings.Current.Ocr.AutoCopyOnOcr;
        return ScreenshotOcrWorkflow.ExecuteAsync(context, presenter, screenshot, ocr,
            ScreenshotPurpose.Ocr, autoCopy, logger, cancellationToken);
    }
}
