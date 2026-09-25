using System;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Infrastructure.DependencyInjection;
using Iris.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Iris.Infrastructure.Tests.DependencyInjection;

public sealed class InfrastructureRegistrationTests
{
    [Fact]
    public void AddIrisInfrastructure_ResolvesSingletonSettingsService()
    {
        using var tempDir = new TempDirectory();
        var services = new ServiceCollection();

        // Prerequisites: IAppPaths, ILogger<>
        services.AddSingleton<IAppPaths>(new FakeAppPaths(tempDir.Path));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddIrisInfrastructure();

        var provider = services.BuildServiceProvider();

        var settings1 = provider.GetService<ISettingsService>();
        var settings2 = provider.GetService<ISettingsService>();

        settings1.ShouldNotBeNull();
        settings2.ShouldNotBeNull();
        settings1.ShouldBeSameAs(settings2);
    }
}
