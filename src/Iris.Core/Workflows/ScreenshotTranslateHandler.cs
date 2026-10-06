using Iris.Core.Actions;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

internal sealed class ScreenshotTranslateHandler(
    ITranslateWindowPresenter presenter, IScreenshotService screenshot, IOcrService ocr,
    ILogger<ScreenshotTranslateHandler> logger) : IActionHandler
{
    public IrisAction Action => IrisAction.ScreenshotTranslate;

    public Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken) =>
        ScreenshotOcrWorkflow.ExecuteAsync(context, presenter, screenshot, ocr,
            ScreenshotPurpose.Translate, autoCopy: false, logger, cancellationToken);
}
