using Iris.App.History;
using Iris.App.Hosting;
using Iris.App.Translation;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

sealed class OwnedPaths:IAppPaths
{
 public bool IsPortable=>true;
 public string DataDirectory{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"iris-history-independent-"+Guid.NewGuid().ToString("N"));
 public string SettingsFile=>System.IO.Path.Combine(DataDirectory,"settings.json");
 public string SecretsFile=>System.IO.Path.Combine(DataDirectory,"secrets.json");
 public string DatabaseFile=>System.IO.Path.Combine(DataDirectory,"history.db");
 public string LogDirectory=>System.IO.Path.Combine(DataDirectory,"logs");
 public OwnedPaths(){System.IO.Directory.CreateDirectory(DataDirectory);}
}
sealed class TrackedRepository(ITranslationHistoryRepository inner):ITranslationHistoryRepository
{
 internal int Queries,Gets,Writes;
 internal Func<HistoryQuery,CancellationToken,Task>? BeforeQuery;
 internal Func<long,CancellationToken,Task>? BeforeGet;
 internal Func<Task>? BeforeWrite;
 internal Func<HistoryQuery,CancellationToken,Task<Iris.Core.Common.PagedResult<HistorySummary>>>? QueryOverride;
 internal Func<long,CancellationToken,Task<HistoryEntry?>>? GetOverride;
 public Task<long> AddAsync(HistoryEntry entry,CancellationToken ct=default)=>inner.AddAsync(entry,ct);
 public async Task<Iris.Core.Common.PagedResult<HistorySummary>> QueryAsync(HistoryQuery query,CancellationToken ct=default)
 {Interlocked.Increment(ref Queries);if(QueryOverride!=null)return await QueryOverride(query,ct);if(BeforeQuery!=null)await BeforeQuery(query,ct);return await inner.QueryAsync(query,ct);}
 public async Task<HistoryEntry?> GetAsync(long id,CancellationToken ct=default)
 {Interlocked.Increment(ref Gets);if(GetOverride!=null)return await GetOverride(id,ct);if(BeforeGet!=null)await BeforeGet(id,ct);return await inner.GetAsync(id,ct);}
 public async Task SetFavoriteAsync(long id,bool favorite,CancellationToken ct=default)
 {Interlocked.Increment(ref Writes);if(BeforeWrite!=null)await BeforeWrite();await inner.SetFavoriteAsync(id,favorite,ct);}
 public async Task DeleteAsync(long id,CancellationToken ct=default)
 {Interlocked.Increment(ref Writes);if(BeforeWrite!=null)await BeforeWrite();await inner.DeleteAsync(id,ct);}
 public async Task ClearAsync(bool keepFavorites,CancellationToken ct=default)
 {Interlocked.Increment(ref Writes);if(BeforeWrite!=null)await BeforeWrite();await inner.ClearAsync(keepFavorites,ct);}
}
sealed class HistoryScope:IAsyncDisposable
{
 internal readonly OwnedPaths Paths=new();
 internal readonly MemorySettings Settings=new(new());
 internal readonly ReviewUi Ui;
 internal readonly IClipboardService Clipboard=Substitute.For<IClipboardService>();
 internal readonly ITranslateWindowPresenter Translation=Substitute.For<ITranslateWindowPresenter>();
 internal readonly List<string> Copies=[];
 internal readonly List<TranslateWindowRequest> Translations=[];
 internal readonly List<Action> Releases=[];
 internal readonly List<Task> Tasks=[];
 internal readonly ServiceProvider StorageRoot,Root;
 internal readonly TrackedRepository Repository;
 internal readonly HistoryWindowPresenter Presenter;
 internal readonly INativeWindowPlacementAdapter Placement;
 internal HistoryViewModel Vm=>Presenter.ActiveSession!.ViewModel!;
 internal HistoryWindow Window=>Presenter.ActiveSession!.Window!;
 private readonly ReviewSta sta;
 internal HistoryScope(ReviewSta sta)
 {
  this.sta=sta;Ui=new(sta);
  var storage=new ServiceCollection();storage.AddSingleton<IAppPaths>(Paths);
  storage.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));storage.AddIrisInfrastructure();
  storage.RemoveAll<ISettingsService>();storage.AddSingleton<ISettingsService>(Settings);
  StorageRoot=storage.BuildServiceProvider();Repository=new(StorageRoot.GetRequiredService<ITranslationHistoryRepository>());
  Clipboard.SetTextAsync(Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(ci=>{Copies.Add(ci.ArgAt<string>(0));return Task.CompletedTask;});
  Translation.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(ci=>
  {
   var operation=Substitute.For<ITranslateWindowOperation>();operation.Id.Returns(Guid.NewGuid());operation.CancellationToken.Returns(ci.Arg<CancellationToken>());return operation;
  });
  Translation.TryShowAsync(Arg.Any<ITranslateWindowOperation>(),Arg.Any<TranslateWindowRequest>()).Returns(ci=>{Translations.Add(ci.Arg<TranslateWindowRequest>());return Task.FromResult(true);});
  var desktop=Substitute.For<IDesktopContext>();
  desktop.GetMonitors().Returns(new[]{new MonitorInfo("Review",new PixelRect(0,0,1920,1080),new PixelRect(0,0,1920,1040),1,true)});
  desktop.GetCursorPosition().Returns(new PixelPoint(30,30));
  var placement=Substitute.For<INativeWindowPlacementAdapter>();Placement=placement;placement.SetWindowBounds(Arg.Any<nint>(),Arg.Any<PixelRect>(),Arg.Any<bool>(),Arg.Any<bool>()).Returns(true);
  var services=new ServiceCollection();services.AddSingleton<ITranslationHistoryRepository>(Repository);
  services.AddSingleton<IClipboardService>(Clipboard);services.AddSingleton<ITranslateWindowPresenter>(Translation);
  services.AddSingleton<IUiDispatcher>(Ui);services.AddSingleton(TimeProvider.System);services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));
  services.AddSingleton<IDesktopContext>(desktop);services.AddSingleton<INativeWindowPlacementAdapter>(placement);
  services.AddScoped<HistoryViewModel>();services.AddScoped<HistoryWindow>(s=>new(s.GetRequiredService<IDesktopContext>(),s.GetRequiredService<INativeWindowPlacementAdapter>())
  {ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,Left=-24000,Top=-24000});
  services.AddSingleton<HistoryWindowPresenter>();Root=services.BuildServiceProvider();Presenter=Root.GetRequiredService<HistoryWindowPresenter>();
 }
 internal async Task ShowAsync(){await Presenter.ShowAsync();Task? query=null;await sta.Run(()=>{query=Vm.QueryTask;return Task.CompletedTask;});await query!;}
 internal T Track<T>(T task)where T:Task{Tasks.Add(task);return task;}
 public async ValueTask DisposeAsync()
 {
  foreach(var release in Releases)release();Ui.Release();Repository.BeforeQuery=null;Repository.BeforeGet=null;Repository.BeforeWrite=null;Repository.QueryOverride=null;Repository.GetOverride=null;
  await Presenter.ShutdownAsync();var errors=new List<Exception>();
  foreach(var task in Tasks)try{await task;}catch(OperationCanceledException){}catch(Exception ex){errors.Add(ex);}
  await Root.DisposeAsync();await StorageRoot.DisposeAsync();
  // Owned temp database retained as review evidence; never touches the user's paths.
  if(errors.Count>0)throw new AggregateException(errors);
 }
}
