using NSubstitute;
using Iris.Core.Storage;
using Iris.Core.Translation;

static class HistoryChecks
{
 static void Check(bool v,string msg){if(!v)throw new Exception(msg);}
 internal static async Task Basic(ReviewSta sta)
 {
  await Scenario(sta,"actual history list uses summaries and full detail for copy/retranslate",async f=>
  {
   string source=new string('文',250)+" unique complete source";
   long id=await f.Repository.AddAsync(new(){CreatedAt=DateTimeOffset.UtcNow,SourceText=source,SourceLanguage="zh",TargetLanguage="en",Origin=TextOrigin.Screenshot,
    Results=[new("provider-a","Old provider label","old-model","complete translation"),new("provider-b","Second",null,"partial translation"){Truncated=true}]});
   await f.ShowAsync();
   Check(f.Repository.Gets==0 && f.Vm.Rows.Count==1,"List eagerly loads details or misses entry");
   await sta.Run(()=>{f.Vm.SelectedRow=f.Vm.Rows.Single();return Task.CompletedTask;});await f.Vm.DetailTask;
   Check(f.Vm.Detail?.SourceText==source && f.Vm.Detail.Results.Count==2 && f.Vm.Detail.Results[1].Truncated,"Detail lost original data");
   await sta.Run(()=>f.Vm.CopySourceAsync());await sta.Run(()=>f.Vm.CopyResultAsync("provider-b"));await sta.Run(()=>f.Vm.RetranslateAsync());
   Check(f.Copies.Count==2 && f.Copies[0]==source && f.Copies[1].StartsWith("partial translation") && f.Copies[1].Contains("[结果已截断]"),"Copy used preview or lost truncation marker");
   Check(f.Translations.Count==1 && f.Translations[0].Text==source && f.Translations[0].Origin==TextOrigin.History && f.Translations[0].AutoTranslate,"Retranslate did not deliver complete original once");
  });
  await Scenario(sta,"last-page delete returns to populated page and canceled clear does not write",async f=>
  {
   var time=DateTimeOffset.UtcNow;
   for(int i=0;i<51;i++)await f.Repository.AddAsync(new(){CreatedAt=time.AddSeconds(i),SourceText="Source "+i,SourceLanguage="en",TargetLanguage="zh",Results=[new("p","Provider",null,"译文 "+i)]});
   await f.ShowAsync();await sta.Run(()=>f.Vm.RequestQueryAsync(1));Check(f.Vm.Rows.Count==1,"Expected one final row");
   await sta.Run(()=>{f.Vm.SelectedRow=f.Vm.Rows.Single();f.Vm.RequestDelete();return Task.CompletedTask;});
   await f.Vm.DetailTask;await sta.Run(()=>f.Vm.ConfirmAsync());await f.Vm.QueryTask;
   Check(f.Vm.PageIndex==0 && f.Vm.Rows.Count==50 && f.Vm.TotalCount==50,"Last page remained out of bounds");
   int writes=f.Repository.Writes;
   await sta.Run(()=>{f.Vm.RequestClear();f.Vm.CancelConfirmation();return f.Vm.ConfirmAsync();});
   Check(f.Repository.Writes==writes,"Cancel confirmation still mutated history");
  });
 }
 internal static async Task Lifecycle(ReviewSta sta)
 {
  await Scenario(sta,"late query cannot replace latest filter and admission remains bounded",async f=>
  {
   await f.ShowAsync();int baseline=f.Repository.Queries;
   var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
   var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Releases.Add(()=>release.TrySetResult());
   int invoked=0;
   f.Repository.QueryOverride=async(q,ct)=>
   {
    if(Interlocked.Increment(ref invoked)==1){entered.TrySetResult();await release.Task;return new([new(101,DateTimeOffset.UtcNow,TextOrigin.Input,"STALE","en","zh",false,1,false)],1);}
    return new([new(202,DateTimeOffset.UtcNow,TextOrigin.Input,q.FavoritesOnly?"LATEST":"WRONG","en","zh",true,1,false)],1);
   };
   Task? old=null,latest=null;
   await sta.Run(()=>{old=f.Track(f.Vm.RefreshAsync());return Task.CompletedTask;});await entered.Task;
   await sta.Run(()=>
   {
    for(int i=0;i<10;i++){f.Vm.FavoritesOnly=i%2==0;f.Track(f.Vm.QueryTask);}
    f.Vm.FavoritesOnly=true;latest=f.Track(f.Vm.QueryTask);return Task.CompletedTask;
   });
   Check(f.Repository.Queries==baseline+1,"Noncooperative query admitted additional concurrent I/O");
   release.TrySetResult();await old!;await latest!;
   Check(invoked==2 && f.Vm.Rows.Single().Id==202 && f.Vm.Rows.Single().Preview=="LATEST","Stale query won or waiting requests accumulated");
  });
  await Scenario(sta,"closing waits for actual clipboard work and duplicate close shares owner",async f=>
  {
   await f.Repository.AddAsync(new(){CreatedAt=DateTimeOffset.UtcNow,SourceText="full source",SourceLanguage="en",TargetLanguage="zh",Results=[new("p","Provider",null,"translation")]});
   await f.ShowAsync();await sta.Run(()=>{f.Vm.SelectedRow=f.Vm.Rows.Single();return Task.CompletedTask;});await f.Vm.DetailTask;
   var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Releases.Add(()=>release.TrySetResult());
   f.Clipboard.SetTextAsync(Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(async ci=>{entered.TrySetResult();await release.Task;});
   Task? copy=null,close=null;var session=f.Presenter.ActiveSession!;
   await sta.Run(()=>{copy=f.Track(f.Vm.CopySourceAsync());return Task.CompletedTask;});await entered.Task;
   await sta.Run(()=>{close=f.Track(session.ShutdownAsync());Check(ReferenceEquals(close,session.ShutdownAsync()),"Duplicate close did not share task");return Task.CompletedTask;});
   Check(!close!.IsCompleted && !session.IsDisposed,"Close completed before clipboard returned");
   release.TrySetResult();await copy!;await close;
   Check(session.IsDisposed && f.Presenter.ActiveSession==null,"Clipboard drain failed to release session");
  });
  await Scenario(sta,"write success UI publication remains owned until shutdown",async f=>
  {
   await f.Repository.AddAsync(new(){CreatedAt=DateTimeOffset.UtcNow,SourceText="source",SourceLanguage="en",TargetLanguage="zh",Results=[new("p","Provider",null,"translation")]});
   await f.ShowAsync();await sta.Run(()=>{f.Vm.SelectedRow=f.Vm.Rows.Single();return Task.CompletedTask;});await f.Vm.DetailTask;
   var paused=f.Ui.Pause(once:true);Task? write=null,close=null;var session=f.Presenter.ActiveSession!;
   await sta.Run(()=>{write=f.Track(f.Vm.ToggleFavoriteAsync());return Task.CompletedTask;});await paused;
   await sta.Run(()=>{close=f.Track(session.ShutdownAsync());return Task.CompletedTask;});
   Check(!close!.IsCompleted && !write!.IsCompleted && !session.IsDisposed,"Shutdown crossed pending write publication");
   f.Ui.Release();await write!;await close;
   Check(session.IsDisposed,"Original write owner not released");
  });
 }
 internal static async Task Preparation(ReviewSta sta)
 {
  foreach(var mode in new[]{"restore","show","close-new"})
  await Scenario(sta,"history screenshot receipt "+mode,async f=>
  {
   int invalidated=0;
   using(var unused=f.Presenter.ObservePreparation(()=>invalidated++)){await unused.HideAsync();}
   Check(f.Presenter.ActiveSession==null,"Screenshot observation created history window");
   await f.ShowAsync();var oldWindow=f.Window;var oldSession=f.Presenter.ActiveSession!;
   using var receipt=f.Presenter.ObservePreparation(()=>invalidated++);await receipt.HideAsync();
   await sta.Run(()=>{Check(!oldWindow.IsVisible,"History did not hide for screenshot");return Task.CompletedTask;});
   if(mode=="show")await f.Presenter.ShowAsync();
   if(mode=="close-new"){await oldSession.ShutdownAsync();await f.ShowAsync();Check(!ReferenceEquals(oldWindow,f.Window),"Reopen reused closed window");}
   await receipt.RestoreAsync(()=>false);
   await sta.Run(()=>
   {
    Check(f.Window.IsVisible,"Old receipt hid current history window");
    if(mode=="close-new")Check(!oldWindow.IsVisible && oldSession.IsDisposed,"Old receipt revived closed history");
    if(mode!="restore")Check(invalidated>0,"New history intent did not invalidate screenshot receipt");
    return Task.CompletedTask;
   });
  });
  await Scenario(sta,"history partial placement failure rolls back before retry",async f=>
  {
   f.Placement.SetWindowBounds(Arg.Any<nint>(),Arg.Any<Iris.Core.Imaging.PixelRect>(),Arg.Any<bool>(),Arg.Any<bool>()).Returns(false);
   Exception? failure=null;try{await f.Presenter.ShowAsync();}catch(Exception ex){failure=ex;}
   Check(failure!=null && f.Presenter.ActiveSession==null,"Partial show failure lost rollback or leaked owner");
   f.Placement.SetWindowBounds(Arg.Any<nint>(),Arg.Any<Iris.Core.Imaging.PixelRect>(),Arg.Any<bool>(),Arg.Any<bool>()).Returns(true);
   await f.ShowAsync();Check(f.Presenter.ActiveSession!=null,"Presenter did not recover after rollback");
  });
 }
 static async Task Scenario(ReviewSta sta,string name,Func<HistoryScope,Task> body)
 {
  var f=await sta.Run(()=>new HistoryScope(sta));Exception? error=null,cleanup=null;
  try{await body(f);}catch(Exception ex){error=ex;}
  try{await f.DisposeAsync();}catch(Exception ex){cleanup=ex;}
  if(error!=null && cleanup!=null)throw new AggregateException(name,error,cleanup);
  if(error!=null)throw new Exception(name,error);if(cleanup!=null)throw new Exception(name+" cleanup",cleanup);
  Console.WriteLine("PASS "+name);
 }
}
