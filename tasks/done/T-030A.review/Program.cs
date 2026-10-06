using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var containers=new List<ServiceProvider>();
int checks=0,failures=0;
RegistryChecks.Check=(name,passed)=>{checks++;if(!passed)failures++;Console.WriteLine($"CHECK {name}={(passed?"PASS":"FAIL")}");};
RegistryChecks.Create=(settings,secrets,factory)=>
{
    var services=new ServiceCollection();
    services.AddSingleton<ISettingsService>(settings);
    services.AddSingleton<ISecretStore>(secrets);
    services.AddSingleton<IOcrProviderFactory>(factory);
    services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));
    services.AddIrisCore();services.AddIrisCore();
    var container=services.BuildServiceProvider();containers.Add(container);
    return container.GetRequiredService<IOcrProviderRegistry>();
};
ServiceProvider ContainerFor(IOcrProviderRegistry registry)=>containers.Single(c=>ReferenceEquals(c.GetRequiredService<IOcrProviderRegistry>(),registry));
CoordinatorChecks.GetCoordinator=registry=>ContainerFor(registry).GetRequiredService<ISettingsEditCoordinator>();
CoordinatorChecks.GetOcrObserverCount=registry=>ContainerFor(registry).GetServices<IProviderConfigurationObserver>().Count(o=>o.GetType().Name=="OcrProviderConfigurationObserver");
var groups=new (string Name,Func<Task> Run)[]
{
    ("lazy-leases",RegistryChecks.LazyAndLeaseAsync),
    ("retry-owner",RegistryChecks.RetrySameOwnerAsync),
    ("stop-waiters",RegistryChecks.StopWaiterCancellationAsync),
    ("publication-race",PublicationChecks.CanceledWaiterAndInvalidatedCandidateAsync),
    ("transient-stop",PublicationChecks.StopDuringTransientConstructionAsync),
    ("credentials",PublicationChecks.CredentialsAsync),
    ("fingerprints",FingerprintChecks.FieldsAndReuseAsync),
    ("retired-references",OwnershipChecks.RetiredReferenceAsync),
    ("multiple-failures",OwnershipChecks.MultipleFailuresAsync),
    ("coordinator-barrier",CoordinatorChecks.SavedButApplyPendingAsync),
};
foreach(var group in groups)
{
    try{await group.Run();}
    catch(Exception error){failures++;Console.WriteLine($"GROUP_FAILURE {group.Name} {error}");}
}
foreach(var container in containers)
{
    try{await container.DisposeAsync();}catch(Exception error){failures++;Console.WriteLine($"CONTAINER_CLEANUP_FAILURE {error}");}
}
Console.WriteLine($"OCR_REGISTRY_REVIEW checks={checks} failures={failures} userContent=False");
return failures==0?0:1;
