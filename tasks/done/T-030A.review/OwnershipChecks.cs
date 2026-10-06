using System.Runtime.CompilerServices;
using Iris.Core.Common;
using Iris.Core.Ocr;
using Iris.Core.Settings;

static class OwnershipChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    static OcrProviderProfile Profile(string id)=>new(){Id=id,Kind=OcrProviderKind.WindowsLocal,DisplayName=id,WindowsOptions=new()};

    internal static async Task RetiredReferenceAsync()
    {
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{Profile("retire"),Profile("control")}),ActiveProviderId="control",FallbackProviderId=null}});
        var factory=new ReviewFactory();var registry=RegistryChecks.Create(settings,new(),factory);
        IOcrProviderSnapshot? current=null;
        try
        {
            var retired=await RetireAsync(registry,settings);
            current=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var control=current.Providers.Single().Provider;var controlWeak=new WeakReference(control);
            factory.ForgetCreated();
            await Task.Yield();
            for(int i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
            RegistryChecks.Check("LIVE_REGISTRY_RELEASES_SUCCESSFULLY_RETIRED_PROVIDER",!retired.IsAlive && controlWeak.IsAlive);
            GC.KeepAlive(control);GC.KeepAlive(registry);
        }
        finally{await RegistryChecks.FinishAsync(registry,factory,current);}
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<WeakReference> RetireAsync(IOcrProviderRegistry registry,ReviewSettings settings)
    {
        IOcrProviderSnapshot? before=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
        IOcrProvider? provider=before.Providers.Single(p=>p.Profile.Id=="retire").Provider;
        var weak=new WeakReference(provider);
        try
        {
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(s.Ocr.Providers.Where(p=>p.Id!="retire").ToArray())}});
            await registry.RefreshAsync().WaitAsync(Limit);
            await before.DisposeAsync();
            return weak;
        }
        finally{provider=null;if(before!=null)await before.DisposeAsync();before=null;}
    }

    internal static async Task MultipleFailuresAsync()
    {
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{Profile("one"),Profile("two")}),ActiveProviderId="one",FallbackProviderId="two"}});
        var factory=new ReviewFactory();var registry=RegistryChecks.Create(settings,new(),factory);
        IOcrProviderSnapshot? before=null;Task? release=null;
        try
        {
            before=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var instances=before.Providers.Select(p=>(ReviewProvider)p.Provider).ToArray();
            foreach(var instance in instances){instance.FailuresRemaining=1;instance.DisposeRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);}
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(Array.Empty<OcrProviderProfile>())}});
            await registry.RefreshAsync().WaitAsync(Limit);
            release=before.DisposeAsync().AsTask();
            await Task.WhenAll(instances.Select(p=>p.DisposeEntered.Task)).WaitAsync(Limit);
            RegistryChecks.Check("MULTIPLE_RETIRED_OWNERS_START_INDEPENDENT_CLEANUP",instances.All(p=>p.ActiveDisposals==1) && !release.IsCompleted);
            foreach(var instance in instances)instance.DisposeRelease!.TrySetResult();
            Exception? failure=null;try{await release.WaitAsync(Limit);}catch(Exception ex){failure=ex;}
            RegistryChecks.Check("MULTIPLE_CLEANUP_FAILURES_ARE_VISIBLE_WITHOUT_RAW_SECRETS",failure!=null && !failure.ToString().Contains(ReviewProvider.FakeSecret,StringComparison.Ordinal) && instances.All(p=>!p.Disposed && p.AsyncDisposals==1));
            await registry.RetryCleanupAsync().WaitAsync(Limit);
            await before.DisposeAsync();
            RegistryChecks.Check("REGISTRY_RETRY_RELEASES_BOTH_ORIGINAL_OWNERS",instances.All(p=>p.Disposed && p.AsyncDisposals==2 && p.PeakDisposals==1 && p.SyncDisposals==0));
        }
        finally
        {
            foreach(var instance in factory.Created)instance.DisposeRelease?.TrySetResult();
            if(release!=null)try{await release.WaitAsync(Limit);}catch(Exception){}
            await RegistryChecks.FinishAsync(registry,factory,before);
        }
    }
}
