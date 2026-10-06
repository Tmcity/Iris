using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

int checks=0,failures=0;var containers=new List<ServiceProvider>();
RegistryChecks.Check=(name,passed)=>{checks++;if(!passed)failures++;Console.WriteLine($"CHECK {name}={(passed?"PASS":"FAIL")}");};
RegistryChecks.Create=(settings,secrets,factory)=>
{
    var services=new ServiceCollection();services.AddSingleton<ISettingsService>(settings);services.AddSingleton<ISecretStore>(secrets);
    services.AddSingleton<IOcrProviderFactory>(factory);services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));services.AddIrisCore();
    var container=services.BuildServiceProvider();containers.Add(container);return container.GetRequiredService<IOcrProviderRegistry>();
};
foreach(var mode in new[]{"normal","owner-failure","early-failure"})
{
    try{await AppChecks.RunAsync(mode);}catch(Exception ex){failures++;Console.WriteLine($"APP_GROUP_FAILURE {mode} {ex}");}
}
foreach(var failure in new[]{false,true})
{
    try{await WindowsChecks.RunAsync(failure);}catch(Exception ex){failures++;Console.WriteLine($"WINDOWS_GROUP_FAILURE {failure} {ex}");}
}
try{await BaiduRegistryChecks.RunAsync();}catch(Exception ex){failures++;Console.WriteLine($"BAIDU_GROUP_FAILURE {ex}");}
try{await HostChecks.RunAsync();}catch(Exception ex){failures++;Console.WriteLine($"HOST_GROUP_FAILURE {ex}");}
foreach(var container in containers)try{await container.DisposeAsync();}catch(Exception ex){failures++;Console.WriteLine($"APP_CONTAINER_FAILURE {ex}");}
Console.WriteLine($"OCR_APP_REVIEW checks={checks} failures={failures} userContent=False");return failures==0?0:1;
