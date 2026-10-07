using System.Text.Json;
using Iris.Core.Security;
using Iris.Core.Settings;

static class CredentialChecks
{
    static int count;
    static void Check(string name,bool pass){count++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");if(!pass)throw new InvalidOperationException(name);}
    static TranslationProviderProfile Translation(string? set=null)=>new(){Id="tp-000000000001",Kind=TranslationProviderKind.OpenAICompatible,DisplayName="Synthetic translation",Model="review-model",BaseUrl="https://example.invalid/v1",OpenAIOptions=new(),CredentialSetId=set,Enabled=false};
    static AppSettings Initial(TranslationProviderProfile t,OcrProviderProfile o)
    {var d=new AppSettings();return d with{Translation=d.Translation with{Providers=[t]},Ocr=d.Ocr with{Providers=[..d.Ocr.Providers,o]}};}
    internal static async Task Run()
    {
        var defaultOcr=new AppSettings().Ocr.Providers[0];
        var t=Translation();var o=defaultOcr with{Id=t.Id,DisplayName="Synthetic OCR"};
        await using(var s=new ReviewScope(Initial(t,o)))
        {
            var plan=new SettingsEditPlan(new(),[new(TranslationProfileChangeKind.Update,t,t,new(CredentialEditKind.Replace,new("translation-key","translation-secret")))])
            {OcrChanges=[new(OcrProfileChangeKind.Update,o,o,new(CredentialEditKind.Replace,new("ocr-key","ocr-secret")))]};
            var result=await s.Track(s.Editor.CommitAsync(plan));var current=s.Settings.Current;
            Check("CROSS_DOMAIN_SAME_ID_COMMIT",result.Status==SettingsEditStatus.Applied && s.Settings.Updates==1 && s.Secrets.Sets==2);
            var tid=current.Translation.Providers.Single().CredentialSetId!;var oid=current.Ocr.Providers.Single(p=>p.Id==o.Id).CredentialSetId!;
            Check("CROSS_DOMAIN_DISTINCT_CREDENTIAL_REFERENCES",tid!=oid);
            using var tj=JsonDocument.Parse(s.Secrets.Get(SecretKeys.CredentialSet(tid))!);using var oj=JsonDocument.Parse(s.Secrets.Get(SecretKeys.CredentialSet(oid))!);
            Check("CROSS_DOMAIN_CREDENTIAL_VALUES_NOT_SWAPPED",tj.RootElement.GetProperty("apiKey").GetString()=="translation-key" && oj.RootElement.GetProperty("apiKey").GetString()=="ocr-key" && oj.RootElement.GetProperty("secretKey").GetString()=="ocr-secret");
        }
        foreach(bool legacy in new[]{false,true})
        {
            var set="cs-00000000000000000000000000000001";t=Translation(legacy?null:set);o=defaultOcr with{Id=t.Id,DisplayName="Synthetic OCR",CredentialSetId=t.CredentialSetId};
            await using var s=new ReviewScope(Initial(t,o));
            if(legacy){s.Secrets.Set(SecretKeys.ApiKey(t.Id),"old-key");s.Secrets.Set(SecretKeys.SecretKey(t.Id),"old-secret");}
            else s.Secrets.Set(SecretKeys.CredentialSet(set),"{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"old-key\",\"secretKey\":\"old-secret\"}");
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[]){OcrChanges=[new(OcrProfileChangeKind.Update,o,o,new(CredentialEditKind.Clear))]}));
            Check("DISABLED_PEER_SHARED_REFERENCE_RETAINED_"+legacy,result.Status==SettingsEditStatus.Applied && s.Secrets.Get(legacy?SecretKeys.ApiKey(t.Id):SecretKeys.CredentialSet(set))!=null);
            Check("CLEAR_DOES_NOT_CHANGE_OTHER_DOMAIN_"+legacy,s.Settings.Current.Translation.Providers.Single()==t && s.Settings.Current.Ocr.Providers.Single(p=>p.Id==o.Id).CredentialSetId!=o.CredentialSetId);
        }
        await using(var s=new ReviewScope())
        {
            var b=s.Settings.Current.Ocr.Providers[0];s.Secrets.Set(SecretKeys.ApiKey(b.Id),"old-key");s.Secrets.Set(SecretKeys.SecretKey(b.Id),"old-secret");s.Observer.Fail=true;
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[]){OcrChanges=[new(OcrProfileChangeKind.Update,b,b,new(CredentialEditKind.Replace,new("new-key","new-secret")))]}));
            Check("PENDING_APPLY_PRESERVES_COMMIT_AND_OLD_VALUES",result.Status==SettingsEditStatus.ApplyPending && result.PendingApply!=null && s.Settings.Updates==1 && s.Secrets.Get(SecretKeys.ApiKey(b.Id))=="old-key");
            int sets=s.Secrets.Sets;s.Observer.Fail=false;var retry=await s.Track(s.Editor.RetryApplyAsync(result.PendingApply!));
            Check("RETRY_APPLY_NO_RESTAGE_OR_RECOMMIT",retry.IsComplete && s.Settings.Updates==1 && s.Secrets.Sets==sets && s.Secrets.Get(SecretKeys.ApiKey(b.Id))==null);
        }
        await using(var s=new ReviewScope())
        {
            using var caller=new CancellationTokenSource();s.Settings.AfterCommit=caller.Cancel;
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[]){Ocr=new(AutoCopyOnOcr:new(false,true))},caller.Token));
            Check("POST_COMMIT_CANCEL_PRESERVES_APPLIED_RESULT",caller.IsCancellationRequested && result.Status==SettingsEditStatus.Applied && s.Settings.Current.Ocr.AutoCopyOnOcr);
        }
        await using(var s=new ReviewScope())
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);s.Releases.Add(()=>release.TrySetResult());
            s.Settings.BeforeUpdate=_=>{entered.TrySetResult();return release.Task;};var b=s.Settings.Current.Ocr.Providers[0];
            var changes=new List<OcrProfileChange>{new(OcrProfileChangeKind.Update,b,b with{DisplayName="captured"},new(CredentialEditKind.Keep))};
            var task=s.Track(s.Editor.CommitAsync(new(new(),[]){OcrChanges=changes}));await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));changes.Clear();release.TrySetResult();
            var result=await task;Check("INPUT_CHANGE_LIST_CAPTURED_BEFORE_AWAIT",result.Status==SettingsEditStatus.Applied && s.Settings.Current.Ocr.Providers[0].DisplayName=="captured");
        }
        Console.WriteLine($"CREDENTIAL_REVIEW checks={count} failures=0");
    }
}
