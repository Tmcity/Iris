using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

/// <summary>
/// 剪贴板翻译动作处理器（Core 层实现）。
/// 完整规则见 TRANSLATION_ENTRYPOINTS.md §4。
/// </summary>
public sealed partial class ClipboardTranslateHandler : IActionHandler
{
    private readonly ITranslateWindowPresenter _presenter;
    private readonly IClipboardService _clipboardService;
    private readonly ILogger<ClipboardTranslateHandler> _logger;

    public ClipboardTranslateHandler(
        ITranslateWindowPresenter presenter,
        IClipboardService clipboardService,
        ILogger<ClipboardTranslateHandler> logger)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IrisAction Action => IrisAction.ClipboardTranslate;

    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        using var operation = _presenter.TryBeginOperation(cancellationToken);
        if (operation == null)
        {
            return;
        }

        string? text = null;
        ErrorInfo? error = null;

        try
        {
            text = await _clipboardService.GetTextAsync(operation.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IrisException ex)
        {
            LogClipboardReadFailed(_logger, ex.Code, ex.GetType().Name);
            error = new ErrorInfo(ex.Code, ErrorMessages.GetDefault(ex.Code));
        }
        catch (Exception ex)
        {
            LogClipboardReadFailed(_logger, ErrorCode.Unknown, ex.GetType().Name);
            error = new ErrorInfo(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown));
        }

        TranslateWindowRequest request;
        if (error != null)
        {
            request = new TranslateWindowRequest
            {
                Text = null,
                Origin = TextOrigin.Clipboard,
                Anchor = context.Anchor,
                AutoTranslate = false,
                FocusInput = false,
                Error = error,
            };
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            request = new TranslateWindowRequest
            {
                Text = string.Empty,
                Origin = TextOrigin.Clipboard,
                Anchor = context.Anchor,
                AutoTranslate = false,
                FocusInput = true,
                Error = new ErrorInfo(ErrorCode.InputEmpty, ErrorMessages.GetDefault(ErrorCode.InputEmpty)),
            };
        }
        else
        {
            request = new TranslateWindowRequest
            {
                Text = text,
                Origin = TextOrigin.Clipboard,
                Anchor = context.Anchor,
                AutoTranslate = true,
                FocusInput = false,
            };
        }

        await _presenter.TryShowAsync(operation, request).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "读取剪贴板失败: ErrorCode={ErrorCode}, ExceptionType={ExceptionType}")]
    private static partial void LogClipboardReadFailed(ILogger logger, ErrorCode errorCode, string exceptionType);
}
