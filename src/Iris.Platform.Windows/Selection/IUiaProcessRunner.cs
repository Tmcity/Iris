using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.Platform.Windows.Selection;

internal interface IUiaProcessSession : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill();
}

internal interface IUiaProcessRunner
{
    Task<IUiaProcessSession> StartAsync(CancellationToken cancellationToken);
}
