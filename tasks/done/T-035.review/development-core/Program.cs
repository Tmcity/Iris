using Iris.Core.DependencyInjection;
using Iris.Core.Settings;
using Iris.Core.Security;
using Iris.Core.Translation;
using Iris.Core.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

static void Check(bool v,string msg){if(!v)throw new Exception(msg);}
var settings=new MemorySettings(new());var secrets=new MemorySecrets();
var observer=Substitute.For<IProviderConfigurationObserver>();
var services=new ServiceCollection();services.AddSingleton<ISettingsService>(settings);services.AddSingleton<ISecretStore>(secrets);
services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));services.AddSingleton(Substitute.For<ITranslationProviderRegistry>());
services.AddSingleton<IOcrProviderFactory>(new TestOcrFactory(OcrProviderKind.BaiduCloud));services.AddSingleton<IOcrProviderFactory>(new TestOcrFactory(OcrProviderKind.WindowsLocal));
services.AddIrisCore();services.RemoveAll<IProviderConfigurationObserver>();services.AddSingleton(observer);
await using var root=services.BuildServiceProvider();var editor=root.GetRequiredService<ISettingsEditCoordinator>();
var initial=settings.Current;
var result=await editor.CommitAsync(new(new(),[]){History=new(new(true,false),new(5000,100))});
Check(result.Status==SettingsEditStatus.Applied && settings.Updates==1 && !settings.Current.History.Enabled && settings.Current.History.MaxEntries==100,"Pure history edit dropped");
Check(observer.ReceivedCalls().Count()==0 && secrets.Sets==0,"Pure history caused observer or credential work");Console.WriteLine("PASS pure history applies without provider side effects");
settings.ExternalChange(s=>s with{History=s.History with{MaxEntries=731}});
result=await editor.CommitAsync(new(new(),[]){History=new(Enabled:new(false,true))});
Check(result.Status==SettingsEditStatus.Applied && settings.Current.History.MaxEntries==731 && settings.Current.History.Enabled,"Null history field overwrote latest");Console.WriteLine("PASS untouched history field preserves current value");
int writes=settings.Updates;
result=await editor.CommitAsync(new(new(),[]){History=new(MaxEntries:new(100,600))});
Check(result.IsConflict && result.Status==SettingsEditStatus.Uncommitted && settings.Updates==writes,"Stale history base accepted");Console.WriteLine("PASS history conflict blocks update");
result=await editor.CommitAsync(new(new(),[]){History=new(MaxEntries:new(100,731))});
Check(result.Status==SettingsEditStatus.Applied,"Already-target history treated as conflict");Console.WriteLine("PASS already-target history edit accepted");
var profile=settings.Current.Ocr.Providers.First(p=>p.Kind==OcrProviderKind.BaiduCloud);
foreach(var max in new[]{99,100001})
{
 writes=settings.Updates;int staged=secrets.Sets;
 result=await editor.CommitAsync(new(new(),[]){History=new(MaxEntries:new(731,max)),OcrChanges=[new(OcrProfileChangeKind.Update,profile,profile,new(CredentialEditKind.Replace,new("probe-key","probe-secret")))]});
 Check(result.Status==SettingsEditStatus.Uncommitted && settings.Updates==writes && secrets.Sets==staged,"Invalid history staged secrets");
}
Console.WriteLine("PASS invalid bounds rejected before credential staging");
settings.BeforeMutate=s=>s with{History=s.History with{MaxEntries=800}};
writes=settings.Updates;
result=await editor.CommitAsync(new(new(),[]){History=new(MaxEntries:new(731,900)),OcrChanges=[new(OcrProfileChangeKind.Update,profile,profile,new(CredentialEditKind.Replace,new("probe-key","probe-secret")))]});
Check(result.Status==SettingsEditStatus.Uncommitted && result.IsConflict && settings.Updates==writes && secrets.Snapshot().Count==0,"Update-time conflict leaked credentials or committed");
Console.WriteLine("PASS update-time conflict rolls back staged credentials");
await root.GetRequiredService<IOcrProviderRegistry>().StopAsync();
