using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Iris.App.Hosting;

/// <summary>
/// 无操作的 WPF 托盘应用主机生命周期实现，替换默认的 ConsoleLifetime。
/// </summary>
internal sealed class WpfHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
