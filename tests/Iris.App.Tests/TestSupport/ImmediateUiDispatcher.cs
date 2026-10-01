using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;

namespace Iris.App.Tests.TestSupport;

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
    {
        return callback();
    }
}
