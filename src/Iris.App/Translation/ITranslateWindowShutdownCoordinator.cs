using System.Threading;
using System.Threading.Tasks;

namespace Iris.App.Translation;

internal interface ITranslateWindowShutdownCoordinator
{
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
