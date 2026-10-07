using Iris.Core.Actions;
using Iris.Core.Presentation;

namespace Iris.Core.Workflows;

public sealed class OpenHistoryHandler(IHistoryWindowPresenter presenter) : IActionHandler
{
    public IrisAction Action => IrisAction.OpenHistory;
    public async Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        await presenter.ShowAsync().ConfigureAwait(false);
    }
}
