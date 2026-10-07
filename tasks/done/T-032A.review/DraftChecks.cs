using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Settings;

static class DraftChecks
{
    static int count;
    static void Check(string name,bool pass){count++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");if(!pass)throw new InvalidOperationException(name);}
    static IOcrDraftTestSession Create(ReviewScope s,bool disabled=false)
    {
        var profile=s.Settings.Current.Ocr.Providers[0];
        var session=s.Editor.CreateOcrDraftTest(new(profile with{Enabled=!disabled},profile,new(CredentialEditKind.Replace,new("synthetic-key","synthetic-secret"))));
        s.Sessions.Add(session);return session;
    }
    internal static async Task Run()
    {
        await using(var s=new ReviewScope())
        {
            var profile=s.Settings.Current.Ocr.Providers[0];int reads=s.Settings.Reads;
            var session=s.Editor.CreateOcrDraftTest(new(profile,profile,new(CredentialEditKind.Replace,new("synthetic-key","synthetic-secret"))));s.Sessions.Add(session);
            Check("DRAFT_CONSTRUCTION_NO_EXTERNAL_WORK",s.Settings.Reads==reads && s.Secrets.Gets==0 && s.Cloud.Created.IsEmpty);
            var result=await s.Track(session.RunAsync());var provider=s.Cloud.Created.Single();
            Check("DRAFT_TEST_ONLY_NO_RECOGNIZE",result.Success && provider.TestCalls==1 && provider.RecognizeCalls==0);
            Check("DRAFT_NO_CONFIGURATION_OR_SECRET_WRITE",s.Settings.Updates==0 && s.Secrets.Sets==0 && s.Observer.Calls==0);
            bool rejected=false;try{await session.RunAsync();}catch(InvalidOperationException){rejected=true;}
            Check("DRAFT_SECOND_RUN_REJECTED",rejected && provider.TestCalls==1);
            await session.DisposeAsync();await session.DisposeAsync();
            Check("DRAFT_SUCCESS_DISPOSES_ORIGINAL_ONCE",provider.DisposeCalls==1);
        }
        await using(var s=new ReviewScope())
        {
            s.Cloud.Configure=p=>p.FailDispose=true;var session=Create(s,true);var result=await s.Track(session.RunAsync());var provider=s.Cloud.Created.Single();
            Check("DISABLED_DRAFT_CAN_TEST",result.Success);
            IrisException? failure=null;try{await session.DisposeAsync();}catch(IrisException ex){failure=ex;}
            Check("DRAFT_DISPOSE_FAILURE_VISIBLE",failure!=null && provider.DisposeCalls==1);
            provider.FailDispose=false;await session.DisposeAsync();
            Check("DRAFT_RETRY_RELEASES_WITHOUT_RETEST",provider.DisposeCalls==2 && provider.TestCalls==1 && s.Cloud.Created.Count==1);
        }
        await using(var s=new ReviewScope())
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s.Releases.Add(()=>release.TrySetResult());
            s.Cloud.Configure=p=>p.Body=async token=>{using var r=token.Register(()=>canceled.TrySetResult());entered.TrySetResult();await release.Task;return new(true,TimeSpan.Zero);};
            var session=Create(s);var run=s.Track(session.RunAsync(),expectedFailure:true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var close=session.DisposeAsync().AsTask();_ = s.Track(close);
            try{await canceled.Task.WaitAsync(TimeSpan.FromSeconds(15));Check("DRAFT_CLOSE_WAITS_NONCOOPERATIVE_TEST",!close.IsCompleted && !run.IsCompleted);}
            finally{release.TrySetResult();}
            bool canceledRun=false;try{await run;}catch(OperationCanceledException){canceledRun=true;}await close;
            Check("DRAFT_CANCELED_RUN_AND_CLOSE_DRAIN",canceledRun && s.Cloud.Created.Single().DisposeCalls==1);
        }
        await using(var s=new ReviewScope())
        {
            CancellationToken token=default;s.Cloud.Configure=p=>{p.FailDispose=true;p.Body=t=>{token=t;return Task.FromResult(new ProviderTestResult(true,TimeSpan.Zero));};};
            var session=Create(s);await s.Track(session.RunAsync());using var callback=token.Register(()=>throw new InvalidOperationException("controlled cancellation callback"));
            IrisException? error=null;try{await session.DisposeAsync();}catch(IrisException ex){error=ex;}
            Check("DRAFT_CANCEL_AND_DISPOSE_ERRORS_OBSERVABLE",error?.Detail?.Contains("CANCEL")==true && error.Detail.Contains("DISPOSE"));
            var provider=s.Cloud.Created.Single();provider.FailDispose=false;await session.DisposeAsync();
            Check("DRAFT_CLEANUP_RETRY_DOES_NOT_REPEAT_TEST",provider.TestCalls==1 && provider.DisposeCalls==2);
        }
        await using(var s=new ReviewScope())
        {
            var profile=s.Settings.Current.Ocr.Providers[0];var session=s.Editor.CreateOcrDraftTest(new(profile,profile,new(CredentialEditKind.Keep)));s.Sessions.Add(session);
            s.Settings.ExternalChange(c=>c with{Ocr=c.Ocr with{Providers=c.Ocr.Providers.Select(p=>p.Id==profile.Id?p with{DisplayName="external"}:p).ToArray()}});
            var result=await s.Track(session.RunAsync());
            Check("DRAFT_KEEP_CONFLICT_PREVENTS_SECRET_READ_OR_PROVIDER",!result.Success && result.Error!=null && s.Secrets.Gets==0 && s.Cloud.Created.IsEmpty);
        }
        Console.WriteLine($"DRAFT_REVIEW checks={count} failures=0");
    }
}
