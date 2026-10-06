using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;

static class PublicationChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    static OcrProviderProfile Profile(string name="before",string? set=null) => new()
        {Id="local",Kind=OcrProviderKind.WindowsLocal,DisplayName=name,CredentialSetId=set,WindowsOptions=new()};
    static AppSettings Settings(OcrProviderProfile profile) => new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{profile}),ActiveProviderId=profile.Id,FallbackProviderId=null}};

    internal static async Task CanceledWaiterAndInvalidatedCandidateAsync()
    {
        using var gate=new FactoryGate();
        using var cancellation=new CancellationTokenSource();
        int creates=0;
        var factory=new ReviewFactory{BeforeCreate=p=>{if(Interlocked.Increment(ref creates)==1)gate.Hold(p);}};
        var settings=new ReviewSettings(Settings(Profile()));var secrets=new ReviewSecrets();
        var registry=RegistryChecks.Create(settings,secrets,factory);
        Task<IOcrProviderSnapshot>? canceled=null;Task<IOcrProviderSnapshot>? survivor=null;
        try
        {
            canceled=registry.AcquireSnapshotAsync(cancellation.Token);
            await gate.Entered.Task.WaitAsync(Limit);
            survivor=registry.AcquireSnapshotAsync();
            cancellation.Cancel();
            bool callerCanceled=false;try{await canceled.WaitAsync(Limit);}catch(OperationCanceledException){callerCanceled=true;}
            RegistryChecks.Check("ACQUIRE_CANCEL_ONLY_LEAVES_ONE_WAITER",callerCanceled && !survivor.IsCompleted);
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(new[]{Profile("after")}),LanguageHint="ja"}});
            gate.Release();
            var snapshot=await survivor.WaitAsync(Limit);
            RegistryChecks.Check("CONSTRUCTION_INVALIDATION_PUBLISHES_ONLY_NEW_GENERATION",snapshot.Providers.Single().Profile.DisplayName=="after" && snapshot.Settings.Ocr.LanguageHint=="ja");
            await registry.RetryCleanupAsync().WaitAsync(Limit);
            var old=factory.Created.Where(p=>p.Descriptor.DisplayName=="before").ToArray();
            RegistryChecks.Check("DISCARDED_CANDIDATE_RELEASES_ACTUAL_INSTANCE",old.Length>=1 && old.All(p=>p.Disposed && p.AsyncDisposals==1 && p.SyncDisposals==0));
        }
        finally
        {
            gate.Release();
            var receivedSnapshots=new List<IOcrProviderSnapshot>();var failures=new List<Exception>();
            foreach(var task in new[]{canceled,survivor})
            {
                if(task==null)continue;
                IOcrProviderSnapshot? received=null;
                try{received=await task.WaitAsync(Limit);}catch(OperationCanceledException){}catch(Exception ex){failures.Add(ex);}
                if(received!=null)receivedSnapshots.Add(received);
            }
            try{await RegistryChecks.FinishAsync(registry,factory,receivedSnapshots.ToArray());}catch(Exception ex){failures.Add(ex);}
            if(failures.Count!=0)throw new AggregateException("Publication scope cleanup failed.",failures);
        }
    }

    internal static async Task StopDuringTransientConstructionAsync()
    {
        using var gate=new FactoryGate();var factory=new ReviewFactory{BeforeCreate=gate.Hold};
        var registry=RegistryChecks.Create(new(Settings(Profile())),new(),factory);
        Task<IOcrProviderSnapshot>? create=null;Task? stop=null;
        IOcrProviderSnapshot? result=null;
        try
        {
            create=registry.CreateTransientAsync(Profile() with{Enabled=false},ProviderCredentials.None);
            await gate.Entered.Task.WaitAsync(Limit);
            registry.StopAccepting();stop=registry.StopAsync();
            RegistryChecks.Check("STOP_WAITS_FOR_ACTUAL_TRANSIENT_CONSTRUCTION",!stop.IsCompleted);
            gate.Release();
            Exception? failure=null;try{result=await create.WaitAsync(Limit);}catch(Exception ex){failure=ex;}
            RegistryChecks.Check("STOPPED_REGISTRY_DOES_NOT_ISSUE_LATE_TRANSIENT_LEASE",result==null && failure!=null && failure is not TimeoutException);
            if(result!=null){await result.DisposeAsync();result=null;}
            await stop.WaitAsync(Limit);
            RegistryChecks.Check("STOP_CLEANS_TRANSIENT_CANDIDATE",factory.Created.All(p=>p.Disposed && p.AsyncDisposals==1));
        }
        finally
        {
            gate.Release();
            var failures=new List<Exception>();
            if(create!=null && result==null)
            {
                try{result=await create.WaitAsync(Limit);}catch(ObjectDisposedException){}catch(OperationCanceledException){}catch(IrisException){}catch(Exception ex){failures.Add(ex);}
            }
            try{await RegistryChecks.FinishAsync(registry,factory,result);}catch(Exception ex){failures.Add(ex);}
            if(stop!=null)try{await stop.WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
            if(failures.Count!=0)throw new AggregateException("Transient scope cleanup failed.",failures);
        }
    }

    internal static async Task CredentialsAsync()
    {
        const string set="cs-0123456789abcdef0123456789abcdef";
        var cases=new (string Name,string? Reference,string? Json,bool Valid,bool Legacy)[]
        {
            ("legacy",null,null,true,true),
            ("valid",set,"{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"new-api\",\"secretKey\":\"new-secret\"}",true,false),
            ("explicit-none",set,"{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":null,\"secretKey\":null}",true,false),
            ("bad-reference"," ",null,false,false),
            ("missing",set,null,false,false),
            ("duplicate",set,"{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":null,\"apiKey\":\"evil\",\"secretKey\":null}",false,false),
            ("wrong-version",set,"{\"format\":\"iris-provider-credentials\",\"version\":2,\"apiKey\":null,\"secretKey\":null}",false,false),
            ("missing-null-field",set,"{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":null}",false,false),
        };
        foreach(var entry in cases)
        {
            var secrets=new ReviewSecrets();secrets.Set(SecretKeys.ApiKey("local"),"legacy-api");secrets.Set(SecretKeys.SecretKey("local"),"legacy-secret");
            if(entry.Json!=null)secrets.Set(SecretKeys.CredentialSet(set),entry.Json);
            var factory=new ReviewFactory();var registry=RegistryChecks.Create(new(Settings(Profile(set:entry.Reference))),secrets,factory);
            IOcrProviderSnapshot? snapshot=null;
            try
            {
                snapshot=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
                bool behavior;
                if(entry.Valid)
                {
                    var actual=factory.Inputs.Single().Credentials;
                    var expected=entry.Legacy ? new ProviderCredentials("legacy-api","legacy-secret") : entry.Name=="explicit-none" ? ProviderCredentials.None : new ProviderCredentials("new-api","new-secret");
                    behavior=actual==expected && snapshot.Providers.Single().Provider.Descriptor.IsConfigured;
                }
                else
                {
                    var provider=snapshot.Providers.Single().Provider;
                    var test=await provider.TestAsync(CancellationToken.None);
                    behavior=factory.Created.IsEmpty && !provider.Descriptor.IsConfigured && !test.Success && test.Error?.Code==ErrorCode.NotConfigured;
                }
                var readLegacy=secrets.ReadKeys.Any(k=>k.StartsWith("provider/",StringComparison.Ordinal));
                RegistryChecks.Check("CREDENTIAL_PROTOCOL_"+entry.Name,behavior && readLegacy==entry.Legacy);
            }
            finally {await RegistryChecks.FinishAsync(registry,factory,snapshot);}
        }
    }
}
