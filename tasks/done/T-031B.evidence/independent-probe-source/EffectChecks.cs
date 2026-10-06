using Iris.App.Services;
using Iris.Core.Presentation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

static class EffectChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    static int count;
    static void Check(string name,bool pass){count++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");if(!pass)throw new InvalidOperationException(name);}
    internal static async Task Run(ReviewSta sta)
    {
        foreach(bool cancelCaller in new[]{false,true})
        {
            await using var s=new PresentationScope(sta);using var caller=new CancellationTokenSource();var op=s.Begin(caller.Token);
            await s.Show(op,new(){StatusMessage="OCR",AutoTranslate=false});await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
            var blocked=s.Ui.Pause(allow:1);
            await s.Show(op,new(){Text="pending copy",AutoTranslate=false,AutoCopySource=true});await blocked.WaitAsync(Limit);
            if(cancelCaller)
            {
                using var later=caller.Token.Register(()=>{s.Ui.Release();s.Presenter.ViewModel!.WaitForActiveWorkAsync().GetAwaiter().GetResult();});
                await Task.Run(caller.Cancel).WaitAsync(Limit);
            }
            else {await sta.Run(()=>{s.Presenter.ViewModel!.InputText="user edit";return Task.CompletedTask;});s.Ui.Release();}
            await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
            Check(cancelCaller?"ORIGINAL_CALLER_REJECTS_PENDING_COPY":"USER_EDIT_REJECTS_PENDING_COPY",!s.Clipboard.ReceivedCalls().Any(c=>c.GetMethodInfo().Name=="SetTextAsync"));
        }
        await using(var s=new PresentationScope(sta))
        {
            var entered=Gate();var release=Gate();var canceled=Gate();s.Release.Add(()=>release.TrySetResult());
            CancellationToken actual=default;
            s.Clipboard.SetTextAsync(Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(async call=>
            {
                actual=call.Arg<CancellationToken>();using var registration=actual.Register(()=>canceled.TrySetResult());
                entered.TrySetResult();await release.Task;
            });
            var op=s.Begin();await s.Show(op,new(){Text="owned copy",AutoTranslate=false,AutoCopySource=true});await entered.Task.WaitAsync(Limit);
            op.Dispose();Check("CALLER_RETURN_PRESERVES_ACCEPTED_COPY",!actual.IsCancellationRequested);
            var stop=s.Presenter.ShutdownAsync();s.Work.Add(stop);
            try{await canceled.Task.WaitAsync(Limit);Check("SHUTDOWN_WAITS_REAL_CLIPBOARD",!stop.IsCompleted);}
            finally{release.TrySetResult();await stop;}
            Check("SHUTDOWN_RELEASES_WINDOW_AFTER_COPY",s.Presenter.Window==null);
        }
        await using(var s=new PresentationScope(sta))
        {
            s.Shell.IsCreated=false;var op=s.Begin();var request=new TranslateWindowRequest{Text="fallback",AutoTranslate=false,NotifyOcrFallback=true};
            await s.Show(op,request);s.Shell.IsCreated=true;await s.Show(op,request);
            Check("UNAVAILABLE_TRAY_DOES_NOT_DEFER_FALLBACK",s.Shell.Shown.IsEmpty);
        }
        foreach(bool stopping in new[]{false,true})
        {
            await using var s=new PresentationScope(sta);using var caller=new CancellationTokenSource();
            var feedback=new ScreenshotOcrFeedback(s.Ui,s.Notifications,NullLogger<ScreenshotOcrFeedback>.Instance);
            try
            {
                var blocked=s.Ui.Pause();var notify=feedback.TryNotifyCopiedAsync(17,caller.Token);await blocked.WaitAsync(Limit);
                if(stopping){feedback.StopAcceptance();s.Ui.Release();}
                else {using var later=caller.Token.Register(()=>{s.Ui.Release();notify.GetAwaiter().GetResult();});await Task.Run(caller.Cancel).WaitAsync(Limit);}
                Check(stopping?"STOP_REJECTS_QUEUED_SILENT_NOTICE":"RAW_CALLER_REJECTS_QUEUED_SILENT_NOTICE",!await notify && s.Shell.Shown.IsEmpty);
            }
            finally{s.Ui.Release();await feedback.StopAsync();feedback.Dispose();}
        }
        Console.WriteLine($"EFFECT_REVIEW checks={count} failures=0");
    }
}
