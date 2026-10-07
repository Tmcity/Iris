using System.Net;
using System.Text.Json;
using Iris.Core.DependencyInjection;
using Iris.Core.Network;
using Iris.Core.Settings;
using Iris.Core.Security;
using Iris.Core.Ocr;
using Iris.Core.Translation;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Iris.Providers.Tests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
if(args.Contains("--dispose-race"))
{
 var body=new GatedDisposeContent();var handler=new ReturnHandler(body,HttpStatusCode.OK);
 await using var f=new Scope(_=>handler);Task? disposing=null,stop=null;bool prematureFault=false,prematureCompletion=false;
 try
 {
  f.Seed("A");using var client=f.Root.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);
  using var request=new HttpRequestMessage(HttpMethod.Get,"http://service.example.test/cleanup-race");
  var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);f.Responses.Add(response);
  disposing=Task.Run(()=>response.Dispose());await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
  stop=f.Manager.StopAsync();await Task.WhenAny(stop,Task.Delay(300));
  prematureFault=stop.IsFaulted && !disposing.IsCompleted;
  prematureCompletion=stop.IsCompleted && !disposing.IsCompleted;
  if(!stop.IsCompleted)Check(ReferenceEquals(stop,f.Manager.StopAsync()),"Concurrent Stop must share in-flight responsibility");
  Console.WriteLine($"DISPOSE_RACE originalDisposePending={!disposing.IsCompleted}; stopFaultedBeforeOwnerCompleted={prematureFault}; retainedContent={f.Manager.ContentCount}; stopFinishedBeforeOwnerCompleted={prematureCompletion}");
 }
 finally
 {
  body.Release.Set();if(disposing!=null)await disposing;
  if(stop!=null)try{await stop;}catch(Iris.Core.Errors.IrisException){}
  await f.Manager.StopAsync();
 }
 Console.WriteLine("DISPOSE_RACE cleanup complete: handlers="+f.Manager.HandlerCount+" contents="+f.Manager.ContentCount);
 if(prematureCompletion)Environment.ExitCode=1;
 return;
}
foreach(var family in new[]{HttpClientNames.Llm,HttpClientNames.Ocr})
{
 await using(var f=new Scope())
 {
  f.Seed("A");f.Wire.Respond=(_,n)=>{if(n==1){f.Seed("B");return "HTTP/1.1 503 Unavailable\r\nRetry-After: 0\r\nContent-Length: 0\r\n\r\n";}return "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";};
  using var client=f.Root.GetRequiredService<IHttpClientFactory>().CreateClient(family);
  using(var request=new HttpRequestMessage(HttpMethod.Get,"http://service.example.test/probe"))f.Responses.Add(await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead));
  Check(f.Wire.Auth.SequenceEqual(new[]{"synthetic-A:password-A","synthetic-A:password-A"}),"Retry changed proxy credential generation");
  using(var request=new HttpRequestMessage(HttpMethod.Get,"http://service.example.test/next"))f.Responses.Add(await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead));
  Check(f.Wire.Auth.Last()=="synthetic-B:password-B" && f.Wire.Endpoints.Count==2,"Existing client did not change pool/credentials for new logical request");
  var stop=f.Manager.StopAsync();Check(!stop.IsCompleted && f.Manager.ContentCount==2,"Stop released live responses");
  foreach(var response in f.Responses)response.Dispose();await stop;
  Check(f.Manager.HandlerCount==0 && f.Manager.ContentCount==0,"Stop retained handler/content after response disposal");
  Console.WriteLine("PASS actual "+family+" same client: retry A, next B, held responses block stop, complete cleanup");
 }
 await using(var f=new Scope())
 {
  f.Seed("A");f.Wire.Respond=(_,_)=>"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: text/event-stream\r\n\r\n"+NetworkWire.ScriptedStream.Chunk("data: first\n\n");
  using var client=f.Root.GetRequiredService<IHttpClientFactory>().CreateClient(family);
  using var request=new HttpRequestMessage(HttpMethod.Get,"http://service.example.test/events");
  var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);f.Responses.Add(response);
  var getting=response.Content.ReadAsStreamAsync();Check(getting.IsCompletedSuccessfully,"Stream acquisition buffered body");
  var stream=await getting;await using var reader=SseParser.ReadEventsAsync(stream,CancellationToken.None).GetAsyncEnumerator();
  Check(await reader.MoveNextAsync() && reader.Current.Data=="first","First SSE not available before EOF");
  var next=reader.MoveNextAsync().AsTask();f.Pending.Add(next);
  Check(!next.IsCompleted,"Second SSE should await scripted bytes");
  var stop=f.Manager.StopAsync();Check(!stop.IsCompleted,"Stop crossed live SSE reader");
  f.Wire.Streams.Last().EmitChunk("data: second\n\n");Check(await next && reader.Current.Data=="second","SSE did not continue during stop drain");
  f.Wire.ReleaseAll();Check(!await reader.MoveNextAsync(),"Expected EOF");
  await reader.DisposeAsync();await stream.DisposeAsync();response.Dispose();await stop;
  Console.WriteLine("PASS actual "+family+" nonbuffered SSE before EOF and stop waits for consumer/response");
 }
}
await using(var f=new Scope())
{
 f.Seed("A");var source=f.Root.GetRequiredService<INetworkProxySnapshotSource>();var uri=new Uri("http://service.example.test/");var first=await source.CaptureCurrentAsync(uri);
 f.Seed("B");var second=await source.CaptureCurrentAsync(uri);
 using(var newer=f.Manager.Acquire(HttpClientNames.Llm,second))using(var older=f.Manager.Acquire(HttpClientNames.Llm,first))
 {Check(f.Manager.ExpectedGeneration==second.GenerationId && !ReferenceEquals(newer.Entry,older.Entry),"Late A changed desired B generation");}
 await f.Manager.StopAsync();Console.WriteLine("PASS real Core capture and manager: delayed A does not roll back B");
}
foreach(var status in new[]{HttpStatusCode.OK,HttpStatusCode.ServiceUnavailable})
{
 var body=new FailedContent();var handler=new ReturnHandler(body,status);
 await using var f=new Scope(_=>handler);
 try
 {
  f.Seed("A");using var client=f.Root.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Llm);
  using var request=new HttpRequestMessage(HttpMethod.Get,"http://service.example.test/cleanup");
  Exception? failure=null;
  try
  {
   var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);f.Responses.Add(response);
   response.Dispose();
  }
  catch(Exception ex){failure=ex;}
  Check(failure!=null && f.Manager.ContentCount>0 && f.Manager.HandlerCount>0,"Failed response cleanup lost original owner");
  int sends=handler.Sends;bool stopFailed=false;try{await f.Manager.StopAsync();}catch{stopFailed=true;}
  Check(stopFailed && f.Manager.ContentCount>0,"Repeated failed cleanup silently succeeded");
  body.Fail=false;await f.Manager.StopAsync();
  Check(handler.Sends==sends && f.Manager.ContentCount==0 && f.Manager.HandlerCount==0 && body.Attempts>=3,"Cleanup retry re-sent request or failed to release original content");
  Console.WriteLine("PASS actual "+status+" content cleanup failure: retained owner, repeated failure, retry without resend, full stop");
 }
 finally{body.Fail=false;}
}
sealed class Scope:IAsyncDisposable
{
 internal readonly MemorySettings Settings=new();internal readonly Secrets Secrets=new();internal readonly NetworkWire Wire=new();
 internal readonly ServiceProvider Root;internal readonly NetworkTransportManager Manager;
 internal readonly List<HttpResponseMessage> Responses=[];internal readonly List<Task> Pending=[];
 internal Scope(Func<NetworkRoute,HttpMessageHandler>? factoryOverride=null)
 {
  var services=new ServiceCollection();services.AddLogging();services.AddSingleton<ISettingsService>(Settings);services.AddSingleton<ISecretStore>(Secrets);services.AddIrisCore();services.AddIrisProviders();
  services.RemoveAll<ITranslationProviderRegistry>();services.AddSingleton(Substitute.For<ITranslationProviderRegistry>());
  services.RemoveAll<IOcrProviderRegistry>();services.AddSingleton(Substitute.For<IOcrProviderRegistry>());services.RemoveAll<IProviderConfigurationObserver>();
  services.AddSingleton<NetworkTransportFactory>(new Factory(Wire,factoryOverride));Root=services.BuildServiceProvider();Manager=Root.GetRequiredService<NetworkTransportManager>();
 }
 internal void Seed(string label)
 {
  string id="pc-"+Guid.NewGuid().ToString("N");const string url="http://proxy.example.test:8080/";
  Secrets.Set("network/proxy-credentials/"+id,JsonSerializer.Serialize(new{format="iris-proxy-credentials",version=1,proxyUri=url,userName="synthetic-"+label,password="password-"+label}));
  Settings.Set(new(){Network=new(){ProxyMode=ProxyMode.Custom,CustomProxyUrl=url,ProxyCredentialSetId=id,BypassProxyOnLocal=false}});
 }
 public async ValueTask DisposeAsync()
 {
  Wire.ReleaseAll();foreach(var task in Pending)try{await task;}catch(OperationCanceledException){}
  foreach(var response in Responses)response.Dispose();await Manager.StopAsync();await Root.DisposeAsync();
 }
 private sealed class Factory(NetworkWire wire,Func<NetworkRoute,HttpMessageHandler>? factoryOverride):NetworkTransportFactory
 {
  internal override HttpMessageHandler Create(NetworkRoute route){if(factoryOverride!=null)return factoryOverride(route);var h=(SocketsHttpHandler)base.Create(route);h.ConnectCallback=wire.ConnectAsync;return h;}
 }
}
sealed class MemorySettings:ISettingsService
{
 public bool IsReadOnly=>false;public AppSettings Current{get;private set;}=new();public event EventHandler<SettingsChangedEventArgs>? Changed;
 internal void Set(AppSettings value){var old=Current;Current=value;Changed?.Invoke(this,new(old,value));}
 public Task LoadAsync(CancellationToken ct=default)=>Task.CompletedTask;public Task UpdateAsync(Func<AppSettings,AppSettings> change,CancellationToken ct=default){Set(change(Current));return Task.CompletedTask;}
}
sealed class Secrets:ISecretStore
{
 private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> values=new();public event EventHandler<string>? Changed;
 public string? Get(string key)=>values.GetValueOrDefault(key);public void Set(string key,string value){values[key]=value;Changed?.Invoke(this,key);}public bool Remove(string key)=>values.TryRemove(key,out _);
}

sealed class FailedContent():ByteArrayContent("response"u8.ToArray())
{
 internal bool Fail=true;internal int Attempts;
 protected override void Dispose(bool disposing){if(disposing){Attempts++;if(Fail)throw new IOException("Controlled content disposal failure");}base.Dispose(disposing);}
}
sealed class ReturnHandler(HttpContent content,HttpStatusCode status):HttpMessageHandler
{
 internal int Sends;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  Sends++;var response=new HttpResponseMessage(status){Content=content};response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);return Task.FromResult(response);
 }
}

sealed class GatedDisposeContent():ByteArrayContent("response"u8.ToArray())
{
 internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal readonly ManualResetEventSlim Release=new();
 protected override void Dispose(bool disposing){if(disposing){Entered.TrySetResult();Release.Wait();}base.Dispose(disposing);}
}
