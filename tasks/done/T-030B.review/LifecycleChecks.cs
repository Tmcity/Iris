using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;

static class LifecycleChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task HeldStageAsync(string stage,bool stopping)
    {
        await using var scope=new PipelineScope();using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);scope.Releases.Add(()=>release.TrySetResult());
        async Task Hold(CancellationToken token){using var registration=token.Register(()=>canceled.TrySetResult());entered.TrySetResult();await release.Task;}
        if(stage=="prepare")scope.Preprocessor.Body=async(image,limits,token)=>{await Hold(token);return image;};
        else scope.Main.Body=async(request,token)=>{await Hold(token);return new OcrResult("main",new[]{new OcrLine("done")},null,TimeSpan.Zero);};
        var work=scope.Run(caller.Token);await entered.Task.WaitAsync(Limit);Task? stop=null;
        if(stopping)stop=scope.Service.StopAsync();else caller.Cancel();
        await canceled.Task.WaitAsync(Limit);
        PipelineScope.Check($"CANCEL_WAITS_ACTUAL_{stage}_{stopping}",!work.IsCompleted && scope.Registry.Issued.Single().Releases==0 && (stop==null || !stop.IsCompleted));
        if(stopping)
        {
            using var waiterCancellation=new CancellationTokenSource();var waiter=scope.Service.StopAsync(waiterCancellation.Token);waiterCancellation.Cancel();
            bool waiterCanceled=false;try{await waiter.WaitAsync(Limit);}catch(OperationCanceledException){waiterCanceled=true;}
            PipelineScope.Check($"STOP_WAITER_CANCEL_IS_LOCAL_{stage}",waiterCanceled && !stop!.IsCompleted);
        }
        release.TrySetResult();bool canceledOutcome=false;try{await work.WaitAsync(Limit);}catch(OperationCanceledException){canceledOutcome=true;}
        if(stop!=null)await stop.WaitAsync(Limit);
        PipelineScope.Check($"CANCEL_RELEASES_SNAPSHOT_WITHOUT_FALLBACK_{stage}_{stopping}",canceledOutcome && scope.Registry.Issued.Single().Releases==1 && scope.Backup.Requests.IsEmpty);
    }

    internal static async Task RetryOriginalSnapshotAsync()
    {
        await using var scope=new PipelineScope();var originalFactory=scope.Registry.Factory;
        scope.Registry.Factory=()=>{var item=originalFactory();item.FailRelease=true;return item;};
        scope.Main.Body=(request,token)=>Task.FromException<OcrResult>(new IrisException(ErrorCode.PermissionDenied,"FAKE_PRIVATE_PRIMARY"));
        Exception? failure=null;try{await scope.Run().WaitAsync(Limit);}catch(Exception ex){failure=ex;}
        var snapshot=scope.Registry.Issued.Single();
        PipelineScope.Check("PRIMARY_AND_CLEANUP_FAILURE_SAFE",failure is IrisException{Code:ErrorCode.Unknown} iris && iris.Detail!.Contains("2002",StringComparison.Ordinal) && !failure.ToString().Contains("FAKE_PRIVATE",StringComparison.Ordinal) && snapshot.Releases==1);
        snapshot.FailRelease=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.ReleaseGate=release;snapshot.OnReleaseAttempt=()=>entered.TrySetResult();scope.Releases.Add(()=>release.TrySetResult());
        var retry=scope.Service.RetryCleanupAsync();await entered.Task.WaitAsync(Limit);var stop=scope.Service.StopAsync();
        PipelineScope.Check("RETRY_AND_STOP_SHARE_ORIGINAL_SNAPSHOT",snapshot.Releases==2 && snapshot.PeakReleases==1 && !retry.IsCompleted && !stop.IsCompleted);
        release.TrySetResult();await Task.WhenAll(retry,stop).WaitAsync(Limit);
        PipelineScope.Check("CLEANUP_RETRY_NEVER_REEXECUTES_OCR",scope.Registry.Issued.Count==1 && scope.Main.Requests.Count==1 && scope.Backup.Requests.IsEmpty && scope.Preprocessor.Calls.Count==1 && snapshot.Releases==2);
    }

    internal static async Task ThrowingCancellationCallbackAsync()
    {
        await using var scope=new PipelineScope();using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var callback=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Releases.Add(()=>release.TrySetResult());
        scope.Main.Body=async(request,token)=>
        {
            using var registration=token.Register(()=>{callback.TrySetResult();throw new InvalidOperationException("FAKE_PRIVATE_CALLBACK");});
            entered.TrySetResult();await release.Task;return new OcrResult("main",new[]{new OcrLine("done")},null,TimeSpan.Zero);
        };
        var work=scope.Run(caller.Token);await entered.Task.WaitAsync(Limit);caller.Cancel();await callback.Task.WaitAsync(Limit);
        PipelineScope.Check("CALLBACK_FAILURE_DOES_NOT_ABANDON_RECOGNITION",!work.IsCompleted && scope.Registry.Issued.Single().Releases==0);
        release.TrySetResult();Exception? failure=null;try{await work.WaitAsync(Limit);}catch(Exception ex){failure=ex;}
        PipelineScope.Check("CALLBACK_FAILURE_SAFE_AND_SNAPSHOT_RELEASED",failure is IrisException{Code:ErrorCode.Unknown} && !failure.ToString().Contains("FAKE_PRIVATE_CALLBACK",StringComparison.Ordinal) && scope.Registry.Issued.Single().Releases==1 && scope.Backup.Requests.IsEmpty);
    }
}
