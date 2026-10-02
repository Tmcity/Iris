using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Translation;

namespace Iris.Core.Workflows;

/// <summary>
/// 传入文本翻译动作处理器（Core 层实现）。
/// 完整规则见 TRANSLATION_ENTRYPOINTS.md §5。
/// </summary>
public sealed class TranslateTextHandler : IActionHandler
{
    private readonly ITranslateWindowPresenter _presenter;

    public TranslateTextHandler(ITranslateWindowPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public IrisAction Action => IrisAction.TranslateText;

    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.Payload))
        {
            throw new IrisException(ErrorCode.InputEmpty, ErrorMessages.GetDefault(ErrorCode.InputEmpty));
        }

        using var operation = _presenter.TryBeginOperation(cancellationToken);
        if (operation == null)
        {
            return;
        }

        var origin = context.Trigger switch
        {
            ActionTrigger.CommandLine => TextOrigin.CommandLine,
            ActionTrigger.DragDrop or ActionTrigger.FloatingBall => TextOrigin.FloatingBall,
            _ => TextOrigin.Input,
        };

        var request = new TranslateWindowRequest
        {
            Text = context.Payload,
            Origin = origin,
            Anchor = context.Anchor,
            AutoTranslate = true,
            FocusInput = false,
        };

        await _presenter.TryShowAsync(operation, request).ConfigureAwait(false);
    }
}
