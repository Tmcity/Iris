using System;
using System.Threading.Tasks;
using Xunit;

[assembly: AssemblyFixture(typeof(Iris.App.Tests.TestSupport.WpfTestAssemblyFixture))]

namespace Iris.App.Tests.TestSupport;

public sealed class WpfTestAssemblyFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync()
        => new(WpfTestRunner.RunAsync(() => Task.CompletedTask));

    public async ValueTask DisposeAsync()
    {
        await WpfTestRunner.TeardownAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
}
