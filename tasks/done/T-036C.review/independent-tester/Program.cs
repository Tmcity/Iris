using System.Net;
using Iris.Core.Network;
using Iris.Core.Settings;
using Iris.Providers.Http;
using Microsoft.Extensions.Logging.Abstractions;

static void Check(bool value,string message){if(!value)throw new Exception(message);}
foreach(var code in new[]{200,401,407,302})
{
 var settings=new Settings();var source=new Source();var factory=new Factory(code);
 var tester=new ProxyConnectivityTester(source,settings,TimeProvider.System,new NetworkRouteResolver(),factory,NullLogger<ProxyConnectivityTester>.Instance);
 var network=new NetworkSettings{ProxyMode=ProxyMode.None};
 var session=tester.Create(new(network,network),new(NetworkEndpointKind.Translation,"selected"));
 Check(settings.Reads==0 && source.Calls==0 && factory.Creates==0,"Create performed I/O or resolved data");
 try
 {
  var result=await session.RunAsync();
  Check(result.HttpStatusCode==code && result.RouteKind==ProxyRouteKind.DirectByMode,"Incorrect status or route");
  Check(result.Status==(code==407?ProxyConnectivityStatus.ProxyAuthenticationRequired:code==302?ProxyConnectivityStatus.RedirectNotFollowed:ProxyConnectivityStatus.EndpointResponded),"Incorrect classification");
  Check(factory.Handler.Calls==1 && factory.Handler.Method==HttpMethod.Head && factory.Handler.AuthHeaders==0,"Probe sent wrong request/auth");
  Check(tester.SessionCount==1 && !factory.Handler.Disposed,"Run incorrectly released ownership");
 }
 finally{await session.DisposeAsync();await tester.StopAsync();tester.Dispose();}
 Check(tester.SessionCount==0 && factory.Handler.Disposed,"Dispose failed to release ownership");
 Console.WriteLine("PASS actual tester status="+code+": pure Create, one HEAD, no origin auth, ownership until Dispose");
}
if(args.Contains("--wire"))
foreach(var mode in new[]{"response","redirect","oversized-headers"})
{
 var wire=new Iris.Providers.Tests.Http.NetworkWire();
 wire.Respond=(request,_)=>
 {
  Check(request.StartsWith("HEAD ",StringComparison.Ordinal),"Probe wire did not use HEAD");
  Check(!request.Contains("\r\nAuthorization:",StringComparison.OrdinalIgnoreCase) && !request.Contains("\r\nCookie:",StringComparison.OrdinalIgnoreCase),"Probe wire carried origin authentication");
  return mode=="redirect"?"HTTP/1.1 302 Found\r\nLocation: http://must-not-follow.example.test/\r\nContent-Length: 0\r\n\r\n":
   mode=="oversized-headers"?"HTTP/1.1 200 OK\r\nX-Large: "+new string('a',20000)+"\r\nContent-Length: 0\r\n\r\n":
   "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
 };
 var source=new Source{UseCustom=true};var factory=new SocketFactory(wire);var tester=new ProxyConnectivityTester(source,new Settings(),TimeProvider.System,new NetworkRouteResolver(),factory,NullLogger<ProxyConnectivityTester>.Instance);
 var network=new NetworkSettings{ProxyMode=ProxyMode.Custom,CustomProxyUrl="http://proxy.example.test:8080/"};
 var session=tester.Create(new(network,network),new(NetworkEndpointKind.Translation,"selected"));
 try
 {
  var result=await session.RunAsync();
  Check(result.Status==(mode=="redirect"?ProxyConnectivityStatus.RedirectNotFollowed:mode=="oversized-headers"?ProxyConnectivityStatus.TransportFailed:ProxyConnectivityStatus.EndpointResponded),"Wire result classification incorrect");
  Check(source.Calls==1 && wire.Endpoints.Count==1 && wire.Endpoints.Single()=="proxy.example.test:8080","Probe repeated capture or followed redirect/different route");
  Check(wire.Auth.Single()=="synthetic-proxy:synthetic-password","Proxy credentials not applied to dedicated route");
  Check(factory.Last!.MaxResponseHeadersLength==16 && factory.Last.ConnectTimeout<=TimeSpan.FromSeconds(10) && !factory.Last.AllowAutoRedirect && !factory.Last.UseCookies,"Probe transport limits missing");
 }
 finally{wire.ReleaseAll();await session.DisposeAsync();await tester.StopAsync();tester.Dispose();}
 Check(tester.SessionCount==0 && wire.Streams.All(x=>x.Disposed),"Probe transport not fully disposed");
 Console.WriteLine("PASS actual Sockets HEAD "+mode+": proxy auth, no origin auth, one route, header limit and full disposal");
}
if(args.Contains("--cancel-callback"))
{
 var settings=new Settings();var source=new Source{ThrowOnCancellation=true};var factory=new Factory(200);
 var tester=new ProxyConnectivityTester(source,settings,TimeProvider.System,new NetworkRouteResolver(),factory,NullLogger<ProxyConnectivityTester>.Instance);
 var network=new NetworkSettings{ProxyMode=ProxyMode.None};var session=tester.Create(new(network,network),new(NetworkEndpointKind.Translation,"selected"));
 await session.RunAsync();bool firstFailed=false,secondFailed=false;
 try{await session.DisposeAsync();}catch{firstFailed=true;}
 source.Registration.Dispose();
 try{await session.DisposeAsync();}catch{secondFailed=true;}
 Console.WriteLine($"CANCEL_CALLBACK firstFailed={firstFailed}; retryFailed={secondFailed}; heldSessions={tester.SessionCount}; handlerDisposed={factory.Handler.Disposed}; sends={factory.Handler.Calls}");
 // Only after recording the product failure, discharge this owned fixture. This is not a passing product result.
 if(secondFailed)
 {
  typeof(ProxyConnectivitySession).GetField("_cancelWork",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,Task.CompletedTask);
  Console.WriteLine("FIXTURE ONLY: previously observed faulted cancellation task replaced for cleanup; not counted as product recovery");
 }
 await session.DisposeAsync();await tester.StopAsync();tester.Dispose();
 Console.WriteLine("FIXTURE CLEANUP sessions="+tester.SessionCount+" handlerDisposed="+factory.Handler.Disposed);
 if(secondFailed)Environment.ExitCode=1;
}
sealed class Settings:ISettingsService
{
 internal int Reads;public bool IsReadOnly=>false;
 public AppSettings Current{get{Reads++;return new(){Translation=new(){Providers=[new(){Id="selected",DisplayName="Selected",Kind=TranslationProviderKind.OpenAICompatible,BaseUrl="http://endpoint.example.test/",Model="",Enabled=false,CredentialSetId="cs-must-not-be-read"}]}};}}
 public event EventHandler<SettingsChangedEventArgs>? Changed{add{}remove{}}
 public Task LoadAsync(CancellationToken ct=default)=>Task.CompletedTask;public Task UpdateAsync(Func<AppSettings,AppSettings> change,CancellationToken ct=default)=>throw new Exception("Probe must not save");
}
sealed class Source:INetworkProxySnapshotSource
{
 internal int Calls;internal bool ThrowOnCancellation;internal bool UseCustom;internal CancellationTokenRegistration Registration;
 public Task<NetworkProxySnapshot> CaptureCurrentAsync(Uri target,CancellationToken ct=default)=>throw new Exception("Probe must not use global snapshot");
 public Task<NetworkProxySnapshot> CaptureDraftAsync(ProxyDraft draft,Uri target,CancellationToken ct=default)
 {
  Calls++;if(ThrowOnCancellation)Registration=ct.Register(()=>throw new InvalidOperationException("Controlled callback failure"));
  return Task.FromResult(UseCustom?new NetworkProxySnapshot(Guid.NewGuid(),target,ProxyMode.Custom,new Uri("http://proxy.example.test:8080/"),new("synthetic-proxy","synthetic-password"),ProxyRouteKind.Proxy):new NetworkProxySnapshot(Guid.NewGuid(),target,ProxyMode.None,null,null,ProxyRouteKind.DirectByMode));
 }
}
sealed class Factory(int status):ProxyConnectivityTransportFactory
{
 internal int Creates;internal readonly Handler Handler=new(status);
 internal override HttpMessageHandler Create(NetworkRoute route){Creates++;return Handler;}
}
sealed class Handler(int status):HttpMessageHandler
{
 internal int Calls,AuthHeaders;internal bool Disposed;internal HttpMethod? Method;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  Calls++;Method=request.Method;AuthHeaders=request.Headers.Count(h=>h.Key is "Authorization" or "Cookie" or "X-Api-Key");
  return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new ByteArrayContent([])});
 }
 protected override void Dispose(bool disposing){if(disposing)Disposed=true;base.Dispose(disposing);}
}

sealed class SocketFactory(Iris.Providers.Tests.Http.NetworkWire wire):ProxyConnectivityTransportFactory
{
 internal SocketsHttpHandler? Last;
 internal override HttpMessageHandler Create(NetworkRoute route){Last=(SocketsHttpHandler)base.Create(route);Last.ConnectCallback=wire.ConnectAsync;return Last;}
}
