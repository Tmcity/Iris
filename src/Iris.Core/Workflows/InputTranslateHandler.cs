using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Presentation;
using Iris.Core.Translation;

namespace Iris.Core.Workflows;

/// <summary>
/// 输入翻译动作处理器（Core 层实现）。
/// 完整规则见 TRANSLATION_ENTRYPOINTS.md §3 与 ARCH §6.2。
/// </summary>
public sealed class InputTranslateHandler : IActionHandler
{
    private readonly ITranslateWindowPresenter _presenter;

    public InputTranslateHandler(ITranslateWindowPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public IrisAction Action => IrisAction.InputTranslate;

    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_presenter.IsVisible && _presenter.IsActive)
        {
            await _presenter.HideAsync().ConfigureAwait(false);
            return;
        }

        using var operation = _presenter.TryBeginOperation(cancellationToken);
        if (operation == null)
        {
            return;
        }

        var request = new TranslateWindowRequest
        {
            Text = context.Payload,
            Origin = TextOrigin.Input,
            Anchor = context.Anchor,
            FocusInput = true,
            AutoTranslate = context.Payload != null,
        };

        await _presenter.TryShowAsync(operation, request).ConfigureAwait(false);
    }
}
