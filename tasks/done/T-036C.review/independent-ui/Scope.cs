using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.Core.DependencyInjection;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Iris.Providers.Tests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

sealed class Paths:IAppPaths
{
 public bool IsPortable=>true;public string DataDirectory{get;}=Path.Combine(Path.GetTempPath(),"iris-t036c-ui-"+Guid.NewGuid().ToString("N"));
 public string SettingsFile=>Path.Combine(DataDirectory,"settings.json");public string SecretsFile=>Path.Combine(DataDirectory,"secrets.dat");public string DatabaseFile=>Path.Combine(DataDirectory,"history.db");public string LogDirectory=>Path.Combine(DataDirectory,"logs");
 public Paths(){Directory.CreateDirectory(DataDirectory);}
}
sealed class Secrets:ISecretStore
{
 internal readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> Values=new();internal int Reads,Writes;
 public event EventHandler<string>? Changed;
 public string? Get(string key){Reads++;return Values.GetValueOrDefault(key);}
 public void Set(string key,string value){Writes++;Values[key]=value;Changed?.Invoke(this,key);}public bool Remove(string key)=>Values.TryRemove(key,out _);
}
sealed class Ui(ReviewSta sta):IUiDispatcher
{
 internal Func<bool>? After;internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public bool CheckAccess()=>sta.CheckAccess();
 public async Task InvokeAsync(Func<Task> callback,CancellationToken token)
 {
  await sta.Run(callback);bool pause=await sta.Run(()=>{if(After?.Invoke()!=true)return false;After=null;return true;});
  if(pause){Entered.TrySetResult();await Release.Task;}
 }
}
sealed class Factory(NetworkWire wire):ProxyConnectivityTransportFactory
{
 internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? Send;internal int Sends;
 internal override HttpMessageHandler Create(NetworkRoute route)
 {if(Send!=null)return new Handler(this);var h=(SocketsHttpHandler)base.Create(route);h.ConnectCallback=wire.ConnectAsync;return h;}
 private sealed class Handler(Factory f):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){f.Sends++;return f.Send!(r,ct);}}
}
sealed class FailContent():ByteArrayContent([])
{internal bool Fail=true;protected override void Dispose(bool disposing){if(disposing&&Fail)throw new IOException("Owned cleanup failure");base.Dispose(disposing);}}
sealed class Scope:IAsyncDisposable
{
 internal readonly Paths Paths=new();internal readonly Secrets Secrets=new();internal readonly NetworkWire Wire=new(){RequireAuth=false};
 internal readonly Ui Ui;internal readonly Factory Factory;internal ServiceProvider Root=null!;internal ISettingsService Settings=null!;
 internal SettingsViewModel Vm=null!;internal SettingsWindow Window=null!;internal SettingsWindowSession Session=null!;internal ProxyConnectivityTester Tester=>Root.GetRequiredService<ProxyConnectivityTester>();
 internal int Updates;internal readonly List<Task> Work=[];internal readonly List<Action> Releases=[];private readonly ReviewSta sta;
 internal Scope(ReviewSta sta){this.sta=sta;Ui=new(sta);Factory=new(Wire);Wire.Respond=(_,_)=>"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";}
 internal async Task Init()
 {
  var initial=new AppSettings{General=new(){Theme=AppTheme.Light},Network=new(){ProxyMode=ProxyMode.None},Translation=new(){Providers=[new(){Id="selected",DisplayName="Saved",Kind=TranslationProviderKind.OpenAICompatible,BaseUrl="http://selected.example.test/",Model="model",Enabled=false}]}};
  await File.WriteAllTextAsync(Paths.SettingsFile,JsonSerializer.Serialize(initial,IrisJson.Options));
  var services=new ServiceCollection();services.AddLogging();services.AddSingleton<IAppPaths>(Paths);services.AddSingleton<ISecretStore>(Secrets);
  services.AddIrisInfrastructure();services.AddIrisCore();services.AddIrisProviders();
  services.RemoveAll<ITranslationProviderRegistry>();services.AddSingleton(Substitute.For<ITranslationProviderRegistry>());
  services.RemoveAll<IOcrProviderRegistry>();services.AddSingleton(Substitute.For<IOcrProviderRegistry>());services.RemoveAll<IProviderConfigurationObserver>();
  services.AddSingleton<ProxyConnectivityTransportFactory>(Factory);services.AddSingleton<IUiDispatcher>(Ui);services.AddSingleton(Substitute.For<IAutoStartService>());
  services.AddSingleton<ThemeService>();services.AddSingleton<SettingsApplyReceiptManager>();services.AddSingleton<ISettingsApplyReceiptManager>(s=>s.GetRequiredService<SettingsApplyReceiptManager>());
  services.AddScoped<GeneralSettingsSaveCoordinator>();services.AddScoped<SettingsViewModel>();Root=services.BuildServiceProvider();Settings=Root.GetRequiredService<ISettingsService>();await Settings.LoadAsync();Settings.Changed+=(_,_)=>Updates++;
  var scope=Root.CreateScope();Vm=scope.ServiceProvider.GetRequiredService<SettingsViewModel>();Window=new(){DataContext=Vm,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,Left=-24000,Top=-24000};
  Session=new(scope,Window,Vm,Root.GetRequiredService<ThemeService>(),scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>());
 }
 internal void Show(){Vm.CurrentPageKey="network";Window.Show();Window.UpdateLayout();Vm.SelectedNetworkEndpoint=Vm.NetworkEndpoints.Single(c=>c.Selection.Kind==NetworkEndpointKind.Translation&&c.Selection.ProviderId=="selected");}
 internal Task Track(Task task){Work.Add(task);return task;}
 public async ValueTask DisposeAsync()
 {
  foreach(var release in Releases)release();Ui.After=null;Ui.Release.TrySetResult();Wire.ReleaseAll();
  foreach(var work in Work)try{await work;}catch(OperationCanceledException){}
  if(Session!=null)await sta.Run(()=>Session.ShutdownAsync());if(Root!=null){await Root.GetRequiredService<INetworkTransportLifetime>().StopAsync();await Root.DisposeAsync();}
 }
}
