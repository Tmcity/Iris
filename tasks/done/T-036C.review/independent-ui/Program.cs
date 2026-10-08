using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using Iris.Core.Settings;
await using var sta=new ReviewSta();
static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
await Scenario("real window paste/test/save",async f=>await sta.Run(async()=>
{
 f.Show();f.Wire.RequireAuth=true;f.Vm.Network.Mode.Value=ProxyMode.Custom;
 ((TextBox)f.Window.FindName("NetworkUrlTextBox")).Text="http://review:password@proxy.example.test:8080/";
 Check(f.Vm.Network.Url.Value=="http://proxy.example.test:8080/" && ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password=="password","Paste did not isolate password");
 await f.Vm.ExecuteNetworkTestAsync();Check(f.Vm.NetworkResultText?.Contains("HTTP 200")==true && f.Updates==0 && f.Secrets.Writes==0,"Test saved config or did not return result");
 f.Vm.History.MaxEntriesText.Value="321";await f.Vm.ExecuteSaveAsync();
 Check(f.Updates==1 && f.Settings.Current.History.MaxEntries==321 && f.Settings.Current.Network.ProxyCredentialSetId!=null,"Joint save did not commit once");
 Check(!f.Vm.IsDirty && ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password.Length==0,"Save retained dirty secrets");
}));
await Scenario("last cleanup publication blocks save",async f=>
{
 var content=new FailContent();f.Releases.Add(()=>content.Fail=false);f.Factory.Send=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
 await sta.Run(async()=>{f.Show();await f.Vm.ExecuteNetworkTestAsync();Check(f.Vm.HasFailedNetworkCleanup,"Expected retained cleanup failure");});
 content.Fail=false;f.Ui.After=()=>!f.Vm.HasFailedNetworkCleanup && f.Tester.SessionCount==0 && f.Vm.HasNetworkTestOwnership;
 Task? retry=null,save=null;await sta.Run(()=>{retry=f.Track(f.Vm.RetryNetworkCleanupAsync());return Task.CompletedTask;});await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
 await sta.Run(()=>{f.Vm.LogLevel="Warning";save=f.Track(f.Vm.ExecuteSaveAsync());return Task.CompletedTask;});
 Check(!retry!.IsCompleted && !save!.IsCompleted && f.Updates==0,"Save crossed original UI publication");
 f.Ui.Release.TrySetResult();await retry!;await save!;Check(f.Updates==1 && f.Factory.Sends==1,"Retry re-sent or failed save");
});
async Task Scenario(string name,Func<Scope,Task> body)
{
 var f=new Scope(sta);Exception? error=null,cleanup=null;
 try{await sta.Run(f.Init);await body(f);}catch(Exception ex){error=ex;}
 try{await f.DisposeAsync();}catch(Exception ex){cleanup=ex;}
 if(error!=null&&cleanup!=null)throw new AggregateException(name,error,cleanup);if(error!=null)throw new Exception(name,error);if(cleanup!=null)throw new Exception(name+" cleanup",cleanup);
 Console.WriteLine("PASS "+name);
}
