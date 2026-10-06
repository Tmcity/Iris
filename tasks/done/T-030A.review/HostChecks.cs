using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

static class HostChecks
{
    internal static async Task RunAsync()
    {
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{new OcrProviderProfile{Id="local",Kind=OcrProviderKind.WindowsLocal,DisplayName="local",WindowsOptions=new()}}),ActiveProviderId="local",FallbackProviderId=null}});
        var factory=new ReviewFactory();
        var builder=Host.CreateApplicationBuilder(new HostApplicationBuilderSettings{DisableDefaults=true});
        builder.Services.AddSingleton<ISettingsService>(settings);builder.Services.AddSingleton<ISecretStore>(new ReviewSecrets());builder.Services.AddSingleton<IOcrProviderFactory>(factory);
        builder.Services.AddIrisCore();builder.Services.AddIrisCore();
        using var host=builder.Build();var registry=host.Services.GetRequiredService<IOcrProviderRegistry>();
        IOcrProviderSnapshot? lease=null;
        try
        {
            lease=await registry.AcquireSnapshotAsync();var provider=(ReviewProvider)lease.Providers.Single().Provider;
            await lease.DisposeAsync();lease=null;await registry.StopAsync();await host.StopAsync();host.Dispose();
            RegistryChecks.Check("ACTUAL_GENERIC_HOST_DISPOSE_AFTER_REGISTRY_DRAIN",provider.Disposed && provider.AsyncDisposals==1 && provider.SyncDisposals==0);
        }
        finally{await RegistryChecks.FinishAsync(registry,factory,lease);}
    }
}
