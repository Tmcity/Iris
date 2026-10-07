using Iris.Core.Settings;

static class HistorySettingsChecks
{
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 internal static async Task Run(ReviewSta sta)
 {
  await Scenario(sta,"history-only save is not dropped",async f=>await sta.Run(async()=>
  {
   f.Show();f.Vm.History.Enabled.Value=false;f.Vm.History.MaxEntriesText.Value="100";
   Check(f.Vm.IsDirty,"History draft is not dirty");
   int calls=f.Observer.Calls;await f.Vm.ExecuteSaveAsync();
   Check(f.Settings.Updates==1 && !f.Settings.Current.History.Enabled && f.Settings.Current.History.MaxEntries==100,"History-only plan did not commit once");
   Check(f.Observer.Calls==calls && !f.Receipts.HasPendingReceipts,"Pure history crossed provider observers");
   Check(!f.Vm.IsDirty && !f.Vm.IsSaving,"Successful save retained dirty/busy state");
  }));
  await Scenario(sta,"invalid current history number preserves draft and blocks joint commit",async f=>await sta.Run(async()=>
  {
   f.Show();f.Vm.LogLevel="Warning";
   foreach(var input in new[]{"","bad","99","100001","1.5","NaN","2147483648"})
   {
    f.Vm.History.MaxEntriesText.Value=input;await f.Vm.ExecuteSaveAsync();
    Check(f.Settings.Updates==0 && f.Vm.History.MaxEntriesText.Value==input && f.Vm.IsDirty,"Invalid history value committed or disappeared");
    Check(!f.Vm.IsSaving && f.Vm.CanEdit,"Invalid history input stranded saving state");
   }
   f.Vm.History.MaxEntriesText.Value="100000";await f.Vm.ExecuteSaveAsync();
   Check(f.Settings.Updates==1 && f.Settings.Current.History.MaxEntries==100000 && f.Settings.Current.General.LogLevel=="Warning","Correction did not commit full plan");
  }));
  await Scenario(sta,"history clean external field survives joint save",async f=>await sta.Run(async()=>
  {
   f.Show();f.Vm.History.Enabled.Value=false;
   f.Settings.ExternalChange(s=>s with{History=s.History with{MaxEntries=731}});
   await f.Ui.InvokeAsync(()=>Task.CompletedTask,CancellationToken.None);
   Check(f.Vm.History.MaxEntriesText.Value=="731","Clean external field did not merge");
   f.Vm.Screenshot.MaskOpacityText.Value="0.7";await f.Vm.ExecuteSaveAsync();
   Check(f.Settings.Updates==1 && !f.Settings.Current.History.Enabled && f.Settings.Current.History.MaxEntries==731 && f.Settings.Current.Screenshot.MaskOpacity==0.7,"Joint save overwrote clean field");
  }));
  await Scenario(sta,"history dirty conflict retains target without committing other domains",async f=>await sta.Run(async()=>
  {
   f.Show();f.Vm.History.MaxEntriesText.Value="600";f.Vm.LogLevel="Warning";
   f.Settings.ExternalChange(s=>s with{History=s.History with{MaxEntries=700}});
   await f.Vm.ExecuteSaveAsync();
   Check(f.Settings.Updates==0 && f.Vm.History.MaxEntriesText.Value=="600" && f.Vm.IsDirty,"History conflict lost target or committed");
   Check(f.Settings.Current.History.MaxEntries==700 && f.Settings.Current.General.LogLevel!="Warning","Conflict changed persisted fields");
  }));
  await Scenario(sta,"history conflict introduced inside update rolls back joint credentials",async f=>await sta.Run(async()=>
  {
   f.Show();var draft=f.Vm.OcrProfiles.Single(p=>p.IsBaidu);f.Vm.SelectedOcrProfile=draft;
   draft.Credentials.Value=CredentialEditKind.Replace;draft.SetReplacementCredentials("review-key","review-secret");
   f.Vm.History.MaxEntriesText.Value="600";
   f.Settings.BeforeMutate=s=>s with{History=s.History with{MaxEntries=700}};
   await f.Vm.ExecuteSaveAsync();
   Check(f.Settings.Updates==0 && f.Settings.Current.History.MaxEntries==700,"Update-time history conflict committed");
   Check(f.Secrets.Snapshot().Count==0 && draft.ReplacementApiKey=="review-key","Conflict leaked staging or discarded uncommitted password");
  }));
  await Scenario(sta,"invalid history rejected before credential staging",async f=>
  {
   var initial=f.Settings.Current;var profile=initial.Ocr.Providers.Single(p=>p.Kind==OcrProviderKind.BaiduCloud);
   var plan=new SettingsEditPlan(new(),[]){History=new(MaxEntries:new(initial.History.MaxEntries,99)),
    OcrChanges=[new(OcrProfileChangeKind.Update,profile,profile,new(CredentialEditKind.Replace,new("review-key","review-secret")))]};
   var result=await f.Editor.CommitAsync(plan);
   Check(result.Status==SettingsEditStatus.Uncommitted && f.Settings.Updates==0 && f.Secrets.Sets==0,"Invalid history staged credentials or committed");
  });
 }
 static async Task Scenario(ReviewSta sta,string name,Func<ReviewScope,Task> body)
 {
  var scope=await sta.Run(()=>new ReviewScope(sta));Exception? error=null,cleanup=null;
  try{await body(scope);}catch(Exception e){error=e;}
  try{await scope.DisposeAsync();}catch(Exception e){cleanup=e;}
  if(error!=null && cleanup!=null)throw new AggregateException(name,error,cleanup);
  if(error!=null)throw new Exception(name,error);if(cleanup!=null)throw new Exception(name+" cleanup",cleanup);
  Console.WriteLine("PASS "+name);
 }
}
