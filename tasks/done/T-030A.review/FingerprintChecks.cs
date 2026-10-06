using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;

static class FingerprintChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task FieldsAndReuseAsync()
    {
        var first=new OcrProviderProfile{Id="one",Kind=OcrProviderKind.WindowsLocal,DisplayName="one",WindowsOptions=new(),BaiduOptions=new()};
        var second=first with{Id="two",DisplayName="two"};
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{first,second}),ActiveProviderId="one",FallbackProviderId="two"}});
        var secrets=new ReviewSecrets();var factory=new ReviewFactory();var registry=RegistryChecks.Create(settings,secrets,factory);
        IOcrProviderSnapshot? current=null,next=null;
        try
        {
            current=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var initial=current.Providers.ToDictionary(p=>p.Profile.Id,p=>p.InstanceId);
            var priorSettings=current.Settings;
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(new[]{second,first}),LanguageHint="ko",TextLayout=OcrTextLayout.KeepLines}});
            await registry.RefreshAsync().WaitAsync(Limit);next=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            RegistryChecks.Check("REORDER_PRESERVES_IDS_AND_LIST_ORDER",next.Providers.Select(p=>p.Profile.Id).SequenceEqual(new[]{"two","one"}) && next.Providers.All(p=>initial[p.Profile.Id]==p.InstanceId));
            RegistryChecks.Check("REQUEST_SETTINGS_CHANGE_DOES_NOT_RECREATE_PROVIDERS",next.Settings.Ocr.LanguageHint=="ko" && current.Settings.Ocr.LanguageHint==priorSettings.Ocr.LanguageHint && factory.Created.Count==2);
            await current.DisposeAsync();current=next;next=null;
            var mutations=new (string Name,Func<OcrProviderProfile,OcrProviderProfile> Change)[]
            {
                ("display",p=>p with{DisplayName="changed"}),
                ("timeout",p=>p with{TimeoutSeconds=31}),
                ("language-tag",p=>p with{WindowsOptions=p.WindowsOptions! with{LanguageTag="ja-JP"}}),
                ("baidu-mode",p=>p with{BaiduOptions=p.BaiduOptions! with{Mode=BaiduOcrMode.General}}),
                ("baidu-direction",p=>p with{BaiduOptions=p.BaiduOptions! with{DetectDirection=false}}),
                ("baidu-paragraph",p=>p with{BaiduOptions=p.BaiduOptions! with{Paragraph=false}}),
                ("baidu-language",p=>p with{BaiduOptions=p.BaiduOptions! with{LanguageType="ENG"}}),
                ("baidu-url",p=>p with{BaiduOptions=p.BaiduOptions! with{BaseUrl="https://example.invalid"}}),
                ("windows-options-null",p=>p with{WindowsOptions=null}),
                ("baidu-options-null",p=>p with{BaiduOptions=null}),
            };
            foreach(var mutation in mutations)
            {
                var previous=current.Providers.Single(p=>p.Profile.Id=="one");
                await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(s.Ocr.Providers.Select(p=>p.Id=="one"?mutation.Change(p):p).ToArray())}});
                await registry.RefreshAsync().WaitAsync(Limit);next=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
                RegistryChecks.Check("PROFILE_FIELD_REBUILDS_"+mutation.Name,next.Providers.Single(p=>p.Profile.Id=="one").InstanceId!=previous.InstanceId && next.Providers.Single(p=>p.Profile.Id=="two").InstanceId==initial["two"]);
                await current.DisposeAsync();current=next;next=null;
            }
            var beforeCredentials=current.Providers.Single(p=>p.Profile.Id=="one").InstanceId;
            secrets.Set(SecretKeys.ApiKey("one"),"FAKE_CHANGED_KEY");
            await registry.RefreshAsync().WaitAsync(Limit);next=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            RegistryChecks.Check("CREDENTIAL_VALUE_REBUILDS_ONLY_OWNER",next.Providers.Single(p=>p.Profile.Id=="one").InstanceId!=beforeCredentials && next.Providers.Single(p=>p.Profile.Id=="two").InstanceId==initial["two"]);
            await current.DisposeAsync();current=next;next=null;
            var beforeUnrelated=current.Providers.ToDictionary(p=>p.Profile.Id,p=>p.InstanceId);
            secrets.Set("credential-set/cs-99999999999999999999999999999999","unreferenced fake");
            await registry.RefreshAsync().WaitAsync(Limit);next=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            RegistryChecks.Check("UNREFERENCED_SECRET_PRESERVES_INSTANCES",next.Providers.All(p=>beforeUnrelated[p.Profile.Id]==p.InstanceId));
        }
        finally{await RegistryChecks.FinishAsync(registry,factory,current,next);}
    }
}
