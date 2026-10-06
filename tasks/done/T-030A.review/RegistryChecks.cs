using Iris.Core.Common;
using Iris.Core.Ocr;
using Iris.Core.Settings;

// Draft checks against signed CCR-030. Bind to actual product DLL only after implementation exists.
static class RegistryChecks
{
    internal static Func<ReviewSettings, ReviewSecrets, ReviewFactory, IOcrProviderRegistry> Create = null!;
    internal static Action<string,bool> Check = null!;
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    static OcrProviderProfile Profile(string id="local", string name="one") => new()
        {Id=id,Kind=OcrProviderKind.WindowsLocal,DisplayName=name,WindowsOptions=new()};
    static AppSettings Settings(params OcrProviderProfile[] profiles) => new()
        {Ocr=new(){Providers=Array.AsReadOnly(profiles),ActiveProviderId=profiles[0].Id,FallbackProviderId=null}};

    internal static async Task FinishAsync(IOcrProviderRegistry registry,ReviewFactory factory,params IOcrProviderSnapshot?[] snapshots)
    {
        var failures=new List<Exception>();
        foreach(var instance in factory.Created){instance.FailuresRemaining=0;instance.DisposeRelease?.TrySetResult();}
        foreach(var snapshot in snapshots)
        {
            if(snapshot==null)continue;
            try{await snapshot.DisposeAsync().AsTask().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
        }
        try{await registry.RetryCleanupAsync().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
        try{await registry.StopAsync().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
        if(failures.Count!=0)throw new AggregateException("Review scope cleanup failed.",failures);
    }

    internal static async Task LazyAndLeaseAsync()
    {
        var settings=new ReviewSettings(Settings(Profile())); var secrets=new ReviewSecrets();var factory=new ReviewFactory();
        var registry=Create(settings,secrets,factory);
        IOcrProviderSnapshot? old=null,second=null,fresh=null;
        try
        {
            Check("REGISTRY_CONSTRUCTION_IS_LAZY",settings.Reads==0 && secrets.ReadKeys.IsEmpty && factory.Created.IsEmpty);
            old=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            second=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var original=old.Providers.Single();var owned=(ReviewProvider)original.Provider;
            await settings.UpdateAsync(s=>s with {Ocr=s.Ocr with {Providers=Array.AsReadOnly(new[]{Profile(name:"two")}),LanguageHint="ja",TextLayout=OcrTextLayout.KeepLines}});
            await registry.RefreshAsync().WaitAsync(Limit);
            fresh=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            Check("OLD_SNAPSHOT_REMAINS_COHERENT",old.Settings.Ocr.LanguageHint!="ja" && old.Providers.Single().Profile.DisplayName=="one" && !owned.Disposed);
            Check("NEW_SNAPSHOT_CAPTURES_CONFIG_AND_INSTANCE",fresh.Settings.Ocr.LanguageHint=="ja" && fresh.Providers.Single().Profile.DisplayName=="two" && fresh.Providers.Single().InstanceId!=original.InstanceId);
            await old.DisposeAsync();old=null;
            Check("FIRST_LEASE_RELEASE_DOES_NOT_DISPOSE",owned.AsyncDisposals==0);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);owned.DisposeRelease=release;
            var disposal=second.DisposeAsync().AsTask();
            try
            {
                await owned.DisposeEntered.Task.WaitAsync(Limit);
                Check("LAST_LEASE_AWAITS_ACTUAL_ASYNC_DISPOSAL",!disposal.IsCompleted && owned.SyncDisposals==0);
                bool inaccessible=false;try{_ = second.Settings;}catch(ObjectDisposedException){inaccessible=true;}
                Check("RELEASING_SNAPSHOT_REJECTS_ACCESS",inaccessible);
            }
            finally {release.TrySetResult();await disposal.WaitAsync(Limit);second=null;}
            Check("RETIRED_INSTANCE_DISPOSED_ONCE",owned.Disposed && owned.AsyncDisposals==1 && owned.PeakDisposals==1);
        }
        finally
        {
            await FinishAsync(registry,factory,old,second,fresh);
        }
    }

    internal static async Task RetrySameOwnerAsync()
    {
        var factory=new ReviewFactory();var registry=Create(new(Settings(Profile())),new(),factory);
        IOcrProviderSnapshot? snapshot=null;
        try
        {
            snapshot=await registry.CreateTransientAsync(Profile(),ProviderCredentials.None).WaitAsync(Limit);
            var instance=(ReviewProvider)snapshot.Providers.Single().Provider;
            instance.FailuresRemaining=1;
            Exception? failure=null;try{await snapshot.DisposeAsync();}catch(Exception ex){failure=ex;}
            Check("TRANSIENT_CLEANUP_FAILURE_VISIBLE_AND_SAFE",failure!=null && !failure.ToString().Contains(ReviewProvider.FakeSecret,StringComparison.Ordinal));
            Check("FAILURE_KEEPS_SAME_UNDISPOSED_OWNER",!instance.Disposed && instance.AsyncDisposals==1);
            await snapshot.DisposeAsync();
            await registry.RetryCleanupAsync().WaitAsync(Limit);
            Check("REPEATED_RELEASE_RETRIES_ORIGINAL_OWNER",instance.Disposed && instance.AsyncDisposals==2 && instance.SyncDisposals==0 && instance.PeakDisposals==1);
        }
        finally
        {
            await FinishAsync(registry,factory,snapshot);
        }
    }

    internal static async Task StopWaiterCancellationAsync()
    {
        var factory=new ReviewFactory();var registry=Create(new(Settings(Profile())),new(),factory);
        IOcrProviderSnapshot? snapshot=null;Task? stop=null;
        try
        {
            snapshot=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var provider=(ReviewProvider)snapshot.Providers.Single().Provider;
            registry.StopAccepting();
            Exception? rejected=null;try{var extra=await registry.AcquireSnapshotAsync();await extra.DisposeAsync();}catch(Exception ex){rejected=ex;}
            Check("STOP_ACCEPTANCE_REJECTS_NEW_BUT_RETAINS_OLD",rejected is ObjectDisposedException && !provider.Disposed);
            using var cts=new CancellationTokenSource();stop=registry.StopAsync();var waiter=registry.StopAsync(cts.Token);cts.Cancel();
            bool canceled=false;try{await waiter.WaitAsync(Limit);}catch(OperationCanceledException){canceled=true;}
            Check("CANCEL_STOP_WAITER_DOES_NOT_COMPLETE_SHARED_STOP",canceled && !stop.IsCompleted && !provider.Disposed);
            await snapshot.DisposeAsync();snapshot=null;await stop.WaitAsync(Limit);
            Check("STOP_COMPLETES_AFTER_FINAL_LEASE",provider.Disposed && provider.AsyncDisposals==1);
        }
        finally
        {
            await FinishAsync(registry,factory,snapshot);
            if(stop!=null)await stop.WaitAsync(Limit);
        }
    }
}
