using Iris.Core.Settings;

static class CommitChecks
{
    internal static int Count;
    static void Check(string name,bool pass){Count++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");if(!pass)throw new InvalidOperationException(name);}
    internal static async Task Run()
    {
        await using(var s=new ReviewScope())
        {
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[])));
            Check("EMPTY_PLAN_HAS_NO_WRITES",result.Status==SettingsEditStatus.Applied && s.Settings.Updates==0 && s.Secrets.Sets==0);
        }
        await using(var s=new ReviewScope())
        {
            var before=s.Settings.Current;
            s.Settings.BeforeMutate=c=>c with{Screenshot=c.Screenshot with{HideIrisWindows=false},Ocr=c.Ocr with{SilentOcrShowNotification=false}};
            var plan=new SettingsEditPlan(new(LogLevel:new(before.General.LogLevel,"Debug")),[])
            {
                Ocr=new(AutoCopyOnOcr:new(false,true),LanguageHint:new(before.Ocr.LanguageHint," en ")),
                Screenshot=new(ShowMagnifier:new(true,false),MaskOpacity:new(.45,.35))
            };
            var result=await s.Track(s.Editor.CommitAsync(plan));var actual=s.Settings.Current;
            Check("JOINT_SCALAR_PLAN_COMMITS_ONCE",result.Status==SettingsEditStatus.Applied && s.Settings.Updates==1);
            Check("JOINT_FIELDS_AND_LANGUAGE_NORMALIZATION",actual.General.LogLevel=="Debug" && actual.Ocr.AutoCopyOnOcr && actual.Ocr.LanguageHint=="en" && actual.Screenshot.MaskOpacity==.35 && !actual.Screenshot.ShowMagnifier);
            Check("UNEDITED_FIELDS_KEEP_LATEST",!actual.Screenshot.HideIrisWindows && !actual.Ocr.SilentOcrShowNotification);
            Check("JOINT_OBSERVER_WITHOUT_SECRET_STAGING",s.Observer.Calls==1 && s.Secrets.Sets==0);
        }
        foreach(double value in new[]{-.01,.91,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            await using var s=new ReviewScope();
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[]){Screenshot=new(MaskOpacity:new(.45,value))}));
            Check("INVALID_MASK_REJECTED_"+value,result.Status==SettingsEditStatus.Uncommitted && s.Settings.Updates==0 && s.Secrets.Sets==0);
        }
        await using(var s=new ReviewScope())
        {
            var profile=s.Settings.Current.Ocr.Providers[0];
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[])
            {
                Ocr=new(FallbackProviderId:new(BuiltInProviderIds.WindowsOcr,BuiltInProviderIds.BaiduOcr)),
                OcrChanges=[new(OcrProfileChangeKind.Update,profile,profile,new(CredentialEditKind.Replace,new("synthetic-key","synthetic-secret")))]
            }));
            Check("INVALID_PRIMARY_FALLBACK_PRECEDES_SECRET_STAGING",result.Status==SettingsEditStatus.Uncommitted && s.Secrets.Sets==0 && s.Settings.Updates==0);
        }
        await using(var s=new ReviewScope())
        {
            var profile=s.Settings.Current.Ocr.Providers[0];
            s.Settings.BeforeMutate=c=>c with{Ocr=c.Ocr with{Providers=c.Ocr.Providers.Select(p=>p.Id==profile.Id?p with{DisplayName="external"}:p).ToArray()}};
            var result=await s.Track(s.Editor.CommitAsync(new(new(),[])
            {
                Screenshot=new(MaskOpacity:new(.45,.35)),
                OcrChanges=[new(OcrProfileChangeKind.Update,profile,profile with{DisplayName="ours"},new(CredentialEditKind.Replace,new("synthetic-key","synthetic-secret")))]
            }));
            Check("SUBMIT_RECHECK_ROLLS_BACK_WHOLE_PLAN",result.Status==SettingsEditStatus.Uncommitted && result.IsConflict && s.Settings.Current.Screenshot.MaskOpacity==.45 && s.Settings.Current.Ocr.Providers[0].DisplayName=="external");
            Check("SUBMIT_CONFLICT_REMOVES_STAGED_SECRET",s.Secrets.Sets==1 && s.Secrets.Snapshot().Count==0);
        }
        Console.WriteLine($"COMMIT_REVIEW checks={Count} failures=0");
    }
}
