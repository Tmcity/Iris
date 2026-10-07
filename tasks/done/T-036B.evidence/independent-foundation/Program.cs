using System.Text.Json;
using Iris.Core.Settings;
using Iris.Core.Security;
using Iris.Core.Storage;
using Iris.Core.Errors;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

int failures=0;
foreach(var scenario in new[]{"userinfo","username-only","mismatched-legacy","malformed-userinfo","direct-update-malformed","future-readonly"})
{
 var paths=new Paths();var secrets=new Secrets();
 var input=new AppSettings{Network=new(){ProxyMode=ProxyMode.Custom,CustomProxyUrl=scenario=="malformed-userinfo"?"http://review-user:review-password@bad host.invalid:8080/":scenario=="userinfo"?"http://review-user:review-password@proxy.example:8080":scenario=="username-only"?"http://review-user@proxy.example:8080":"http://proxy.example:8080/",BypassProxyOnLocal=false}};
 var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
 string original=scenario=="future-readonly"?"{\"schemaVersion\":999999999999999999999,\"network\":{\"customProxyUrl\":\"http://u:p@proxy.example\"}}":JsonSerializer.Serialize(input,options);
 await File.WriteAllTextAsync(paths.SettingsFile,original);
 if(scenario=="mismatched-legacy")secrets.Values["network/proxy-credentials"]=JsonSerializer.Serialize(new{format="iris-proxy-credentials",version=1,proxyUri="http://another-proxy.example:8080/",userName="review-user",password="review-password"});
 var services=new ServiceCollection();services.AddLogging();services.AddSingleton<IAppPaths>(paths);services.AddSingleton<ISecretStore>(secrets);services.AddIrisInfrastructure();
 await using var root=services.BuildServiceProvider();var settings=root.GetRequiredService<ISettingsService>();Exception? error=null;
 try{await settings.LoadAsync();}catch(Exception ex){error=ex;}
 bool passed;
 if(scenario=="direct-update-malformed")
 {
  Exception? updateError=null;
  try{await settings.UpdateAsync(s=>s with{Network=s.Network with{CustomProxyUrl="http://review-user:review-password@bad host.invalid:8080/"}});}catch(Exception ex){updateError=ex;}
  bool persisted=(await File.ReadAllTextAsync(paths.SettingsFile)).Contains("review-password");
  Console.WriteLine($"DIRECT_UPDATE updateError={updateError?.GetType().Name??"none"}; newCurrentContainsSyntheticPassword={persisted}");
  passed=error==null && updateError is IrisException && !persisted;
 }
 else if(scenario=="malformed-userinfo")
 {
  bool unsafeBackup=false;
  if(error==null)
  {
   await settings.UpdateAsync(s=>s with{General=s.General with{LogLevel="Warning"}});
   unsafeBackup=File.Exists(paths.SettingsFile+".bak") && (await File.ReadAllTextAsync(paths.SettingsFile+".bak")).Contains("review-password");
  }
  passed=error is IrisException{Code:ErrorCode.StorageFailure} && await File.ReadAllTextAsync(paths.SettingsFile)==original;
  Console.WriteLine("MALFORMED_USERINFO newBackupContainsSyntheticPassword="+unsafeBackup);
 }
 else if(scenario=="mismatched-legacy")passed=error is IrisException{Code:ErrorCode.StorageFailure} && await File.ReadAllTextAsync(paths.SettingsFile)==original && secrets.Values.Count==1;
 else if(scenario=="future-readonly")
 {
  int mutations=0;try{await settings.UpdateAsync(s=>{mutations++;return s;});}catch(IrisException){}
  passed=error==null && settings.IsReadOnly && mutations==0 && await File.ReadAllTextAsync(paths.SettingsFile)==original && secrets.Reads==0 && secrets.Writes==0;
 }
 else
 {
  var id=settings.Current.Network.ProxyCredentialSetId;
  passed=error==null && id!=null && !new Uri(settings.Current.Network.CustomProxyUrl!).UserInfo.Any();
  if(passed)
  {
   var envelope=JsonDocument.Parse(secrets.Values["network/proxy-credentials/"+id]);
   passed=envelope.RootElement.GetProperty("password").GetString()==(scenario=="username-only"?"":"review-password");
  }
 }
 Console.WriteLine($"{(passed?"PASS":"FAIL")} {scenario}: loadError={error?.GetType().Name??"none"}; readonly={settings.IsReadOnly}; secretReads={secrets.Reads}; secretWrites={secrets.Writes}");
 if(!passed)failures++;
}
Console.WriteLine("CHECK_FAILURES="+failures);Environment.ExitCode=failures==0?0:1;
sealed class Paths:IAppPaths
{
 public bool IsPortable=>true;public string DataDirectory{get;}=Path.Combine(Path.GetTempPath(),"iris-t036b-review-"+Guid.NewGuid().ToString("N"));
 public string SettingsFile=>Path.Combine(DataDirectory,"settings.json");public string SecretsFile=>Path.Combine(DataDirectory,"secrets.dat");public string DatabaseFile=>Path.Combine(DataDirectory,"history.db");public string LogDirectory=>Path.Combine(DataDirectory,"logs");
 public Paths(){Directory.CreateDirectory(DataDirectory);}
}
sealed class Secrets:ISecretStore
{
 internal Dictionary<string,string> Values=[];internal int Reads,Writes;
 public event EventHandler<string>? Changed{add{}remove{}}
 public string? Get(string key){Reads++;return Values.GetValueOrDefault(key);}
 public void Set(string key,string value){Writes++;Values[key]=value;}
 public bool Remove(string key)=>Values.Remove(key);
}
