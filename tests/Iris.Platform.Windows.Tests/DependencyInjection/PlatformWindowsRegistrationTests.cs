using Iris.Core.Security;
using Iris.Core.Storage;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Platform.Windows.Security;
using Iris.Platform.Windows.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.DependencyInjection;

public sealed class PlatformWindowsRegistrationTests
{
    [Fact]
    public void AddIrisPlatformWindows_RegistersISecretStoreAsSingleton()
    {
        using var dir = new TempDirectory();
        var paths = new FakeAppPaths(dir.Path);

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddIrisPlatformWindows();

        var provider = services.BuildServiceProvider();

        var store1 = provider.GetService<ISecretStore>();
        var store2 = provider.GetService<ISecretStore>();

        store1.ShouldNotBeNull();
        store2.ShouldNotBeNull();
        store1.ShouldBeOfType<DpapiSecretStore>();
        ReferenceEquals(store1, store2).ShouldBeTrue();
    }
}
