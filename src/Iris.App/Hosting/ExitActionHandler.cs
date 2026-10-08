using Iris.Core.Actions;
using Iris.Core.Presentation;

namespace Iris.App.Hosting;

// Exit is an App lifecycle action; requesting shutdown must return so its own Dispatch can drain.
internal sealed class ExitActionHandler(IAppLifetime lifetime) : IActionHandler
{
    public IrisAction Action => IrisAction.Exit;
    public Task ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); lifetime.RequestShutdown();
        return Task.CompletedTask;
    }
}
