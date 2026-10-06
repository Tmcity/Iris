using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;

static class CoordinatorChecks
{
    internal static Func<IOcrProviderRegistry,ISettingsEditCoordinator> GetCoordinator=null!;
    internal static Func<IOcrProviderRegistry,int> GetOcrObserverCount=null!;
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);

    internal static async Task SavedButApplyPendingAsync()
    {
        const string oldSet="cs-1234567890abcdef1234567890abcdef";
        var oldTranslation=new TranslationProviderProfile{Id="tp-111111111111",Kind=TranslationProviderKind.OpenAICompatible,DisplayName="disabled old",Enabled=false,Model="fake-model",BaseUrl="https://example.invalid",CredentialSetId=oldSet};
        var settings=new ReviewSettings(new(){Translation=new(){Providers=Array.AsReadOnly(new[]{oldTranslation})},Ocr=new(){Providers=Array.AsReadOnly(new[]{new OcrProviderProfile{Id="local",Kind=OcrProviderKind.WindowsLocal,DisplayName="local",WindowsOptions=new()}}),ActiveProviderId="local",FallbackProviderId=null}});
        var secrets=new ReviewSecrets();secrets.Set(SecretKeys.CredentialSet(oldSet),"{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"FAKE_OLD_KEY\",\"secretKey\":null}");
        int fail=1;
        var factory=new ReviewFactory{BeforeCreate=_=>{if(Volatile.Read(ref fail)!=0)throw new InvalidOperationException(ReviewProvider.FakeSecret);}};
        var registry=RegistryChecks.Create(settings,secrets,factory);
        IOcrProviderSnapshot? snapshot=null;
        try
        {
            var coordinator=GetCoordinator(registry);
            RegistryChecks.Check("REPEATED_CORE_REGISTRATION_HAS_ONE_OCR_OBSERVER",GetOcrObserverCount(registry)==1);
            var was=settings.Current.General.ShowStartupNotification;
            var plan=new SettingsEditPlan(new(ShowStartupNotification:new(was,!was)),Array.AsReadOnly(new[]{new TranslationProfileChange(TranslationProfileChangeKind.Delete,oldTranslation,null,new(CredentialEditKind.Keep))}),new(Array.AsReadOnly(new[]{oldTranslation.Id}),Array.AsReadOnly(Array.Empty<string>())));
            var committed=await coordinator.CommitAsync(plan).WaitAsync(Limit);
            RegistryChecks.Check("OCR_OBSERVER_FAILURE_PRESERVES_COMMITTED_SETTINGS",committed.Status==SettingsEditStatus.ApplyPending && committed.PendingApply!=null && settings.Updates==1 && settings.Current.General.ShowStartupNotification==!was && settings.Current.Translation.Providers.Count==0);
            RegistryChecks.Check("OCR_FAILED_BARRIER_RETAINS_OLD_CREDENTIAL_SET",secrets.Get(SecretKeys.CredentialSet(oldSet))!=null);
            if(committed.PendingApply==null)throw new InvalidOperationException("Expected actual Coordinator pending receipt.");
            var stillFailed=await coordinator.RetryApplyAsync(committed.PendingApply).WaitAsync(Limit);
            RegistryChecks.Check("FAILED_RETRY_DOES_NOT_REPEAT_COMMIT_OR_DELETE",!stillFailed.IsComplete && settings.Updates==1 && secrets.Get(SecretKeys.CredentialSet(oldSet))!=null);
            Volatile.Write(ref fail,0);
            var applied=await coordinator.RetryApplyAsync(committed.PendingApply).WaitAsync(Limit);
            RegistryChecks.Check("SUCCESSFUL_OCR_BARRIER_ALLOWS_CREDENTIAL_CLEANUP",applied.IsComplete && settings.Updates==1 && secrets.Get(SecretKeys.CredentialSet(oldSet))==null);
            snapshot=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            RegistryChecks.Check("OBSERVER_AND_CONSUMER_SHARE_REGISTRY_INSTANCE",factory.Created.Count==1 && ReferenceEquals(snapshot.Providers.Single().Provider,factory.Created.Single()));
        }
        finally{Volatile.Write(ref fail,0);await RegistryChecks.FinishAsync(registry,factory,snapshot);}
    }
}
