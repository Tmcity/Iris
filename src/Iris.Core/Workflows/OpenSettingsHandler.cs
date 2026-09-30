using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Presentation;

namespace Iris.Core.Workflows;

/// <summary>
/// 打开设置窗口动作处理器（Core 层实现）。
/// 完整规则见 SETTINGS_EDITING.md §2。
/// </summary>
public sealed class OpenSettingsHandler : IActionHandler
{
    private readonly ISettingsWindowPresenter _presenter;

    public OpenSettingsHandler(ISettingsWindowPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public IrisAction Action => IrisAction.OpenSettings;

    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        await _presenter.ShowAsync(context.Payload).ConfigureAwait(false);
    }
}
