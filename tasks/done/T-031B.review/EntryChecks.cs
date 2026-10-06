using System.Collections.Concurrent;
using System.Reflection;
using System.IO;
using Iris.App.Services;
using Iris.App.Hosting;
using Iris.Core.Actions;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

static class EntryChecks
{
    internal static int Count;
    static void Check(string name,bool pass)
    {Count++;Console.WriteLine($"CHECK {name}={(pass?"PASS":"FAIL")}");if(!pass)throw new InvalidOperationException(name);}
    static IActionHandler Handler(string name,params object[] services)
    {
        var type=typeof(IActionHandler).Assembly.GetType("Iris.Core.Workflows."+name,true)!;
        var ctor=type.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Single();
        var args=ctor.GetParameters().Select(p=>p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition()==typeof(ILogger<>)
            ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(p.ParameterType.GenericTypeArguments))!
            : services.Single(p.ParameterType.IsInstanceOfType)).ToArray();
        return (IActionHandler)ctor.Invoke(args);
    }
    static ScreenshotResult Image() => new(new ImageData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","small-colors.png")),ImageFormat.Png,240,160),new(-300,20,240,160));
    internal static async Task RunExclusivity(ReviewSta sta)
    {
        foreach(var active in new[]{IrisAction.ScreenshotOcr,IrisAction.ScreenshotTranslate,IrisAction.SilentScreenshotOcr})
        {
            await using var s=new PresentationScope(sta);var capture=new CaptureBoundary();var recognition=new RecognitionBoundary();var proxy=new RecordingPresenter(s);
            var feedback=new ScreenshotOcrFeedback(s.Ui,s.Notifications,NullLogger<ScreenshotOcrFeedback>.Instance);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s.Release.Add(()=>release.TrySetResult());capture.Body=(_,_)=>Task.FromResult<ScreenshotResult?>(Image());
            recognition.Body=async(_,_,_)=>{entered.TrySetResult();await release.Task;return new(new("review",[],null,TimeSpan.Zero),"exclusive",false);};
            var handlers=new[]{Handler("ScreenshotOcrHandler",proxy,capture,recognition,s.Settings),Handler("ScreenshotTranslateHandler",proxy,capture,recognition),Handler("SilentScreenshotOcrHandler",capture,recognition,s.Clipboard,s.Settings,feedback)};
            var dispatcher=new ActionDispatcher(handlers,s.Ui,s.Notifications,NullLogger<ActionDispatcher>.Instance);
            var task=dispatcher.DispatchAsync(active,new(ActionTrigger.InApp));s.Work.Add(task);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                using var ignoredCaller=new CancellationTokenSource();
                var ignored=handlers.Select(h=>dispatcher.DispatchAsync(h.Action,new(ActionTrigger.InApp),ignoredCaller.Token)).ToArray();
                Check($"{active}_OTHER_SCREENSHOT_REQUESTS_DROP_IMMEDIATELY",ignored.All(t=>t.IsCompletedSuccessfully));
                ignoredCaller.Cancel();
                Check($"{active}_NO_SECOND_CAPTURE_OR_OCR",capture.Calls.Count==1 && recognition.Calls.Count==1);
                Check($"{active}_IGNORED_CANCELLATION_PRESERVES_ACTIVE",!capture.Calls.Single().Token.IsCancellationRequested && !task.IsCompleted);
            }
            finally
            {
                release.TrySetResult();await task;dispatcher.StopAcceptance();await dispatcher.WaitForInFlightAsync();await feedback.StopAsync();feedback.Dispose();
            }
        }
        Console.WriteLine($"DISPATCHER_REVIEW checks={Count} failures=0");
    }
    internal static async Task Run(ReviewSta sta)
    {
        foreach(bool translate in new[]{false,true})
        {
            await using var s=new PresentationScope(sta);var capture=new CaptureBoundary();var recognition=new RecognitionBoundary();var proxy=new RecordingPresenter(s);
            var image=Image();capture.Body=(_,_)=>Task.FromResult<ScreenshotResult?>(image);
            recognition.Body=(_,_,_)=>Task.FromResult(new OcrOutcome(new("review",[],null,TimeSpan.Zero),"OCR source 中文",true));
            s.Settings.Current.Returns(new AppSettings{Ocr=new(){AutoCopyOnOcr=true}});
            var handler=Handler(translate?"ScreenshotTranslateHandler":"ScreenshotOcrHandler",proxy,capture,recognition,s.Settings);
            var task=handler.ExecuteAsync(new(ActionTrigger.InApp),CancellationToken.None);s.Work.Add(task);await task;
            await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
            Check($"{handler.Action}_PURPOSE_AND_ORIGINAL_IMAGE",capture.Calls.Single().Purpose==(translate?ScreenshotPurpose.Translate:ScreenshotPurpose.Ocr) && ReferenceEquals(recognition.Calls.Single().Image,image.Image));
            var requests=proxy.Requests.ToArray();
            Check($"{handler.Action}_LOADING_THEN_TEXT_SAME_OPERATION",requests.Length==2 && requests[0].Operation==requests[1].Operation && requests[0].Request.Text==null && !string.IsNullOrEmpty(requests[0].Request.StatusMessage) && requests[1].Request.Text=="OCR source 中文");
            Check($"{handler.Action}_PHYSICAL_ANCHOR",requests.All(r=>r.Request.Anchor==new PixelPoint(-60,180)));
            Check($"{handler.Action}_REAL_ORIGINAL_TEXT",await sta.Run(()=>s.Presenter.ViewModel!.InputText)=="OCR source 中文");
            Check($"{handler.Action}_TRANSLATION_COUNT",s.Translation.Requests.Count==(translate?1:0));
            var writes=s.Clipboard.ReceivedCalls().Where(c=>c.GetMethodInfo().Name==nameof(s.Clipboard.SetTextAsync)).ToArray();
            Check($"{handler.Action}_COPY_SOURCE_SETTING",writes.Length==(translate?0:1));
            Check($"{handler.Action}_FALLBACK_NOTICE_ONCE",s.Shell.Shown.Count==1 && s.Shell.Shown.Single().Message=="已使用备用 OCR");
            if(!translate)
            {
                bool enabled=await sta.Run(()=>
                {
                    IEnumerable<System.Windows.Controls.Button> Find(System.Windows.DependencyObject node)
                    {
                        if(node is System.Windows.Controls.Button b)yield return b;
                        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)
                            foreach(var child in Find(System.Windows.Media.VisualTreeHelper.GetChild(node,i)))yield return child;
                    }
                    var button=Find(s.Presenter.Window!).Single(b=>ReferenceEquals(b.Command,s.Presenter.ViewModel!.TranslateCommand));
                    bool result=button.IsEnabled;if(result)button.Command.Execute(null);return result;
                });
                Check("OCR_REAL_TRANSLATE_BUTTON_ENABLED",enabled);
                await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
                Check("OCR_MANUAL_CONTINUE_TRANSLATES_ONCE",s.Translation.Requests.Count==1 && s.Windows.Count==1);
            }
        }
        await using(var s=new PresentationScope(sta))
        {
            var old=s.Begin();await s.Show(old,new(){Text="Existing translation input",AutoTranslate=false});
            var capture=new CaptureBoundary();var recognition=new RecognitionBoundary();capture.Body=(_,_)=>Task.FromResult<ScreenshotResult?>(Image());
            var feedback=new ScreenshotOcrFeedback(s.Ui,s.Notifications,NullLogger<ScreenshotOcrFeedback>.Instance);
            try
            {
                var handler=Handler("SilentScreenshotOcrHandler",capture,recognition,s.Clipboard,s.Settings,feedback);
                var task=handler.ExecuteAsync(new(ActionTrigger.InApp),CancellationToken.None);s.Work.Add(task);await task;
                Check("SILENT_PRESERVES_TRANSLATION_IDENTITY",!old.CancellationToken.IsCancellationRequested && await sta.Run(()=>s.Presenter.ViewModel!.InputText)=="Existing translation input");
                Check("SILENT_NO_NEW_WINDOW_OR_TRANSLATION",s.Windows.Count==1 && s.Translation.Requests.IsEmpty);
                Check("SILENT_CAPTURE_PURPOSE",capture.Calls.Single().Purpose==ScreenshotPurpose.SilentOcr);
                await s.Clipboard.Received(1).SetTextAsync("Review OCR text",Arg.Any<CancellationToken>());
                Console.WriteLine("SILENT_MESSAGES="+string.Join(" | ",s.Shell.Shown.Select(n=>n.Message)));
                Check("SILENT_FIXED_COUNT_NOTICE",s.Shell.Shown.Count==1 && s.Shell.Shown.Single().Message.Contains("15") && !s.Shell.Shown.Single().Message.Contains("Review OCR text"));
            }
            finally{await feedback.StopAsync();feedback.Dispose();}
        }
        Console.WriteLine($"ENTRY_REVIEW checks={Count} failures=0");
    }

    sealed class RecordingPresenter(PresentationScope scope):ITranslateWindowPresenter
    {
        internal readonly ConcurrentQueue<(Guid Operation,TranslateWindowRequest Request)> Requests=new();
        public bool IsVisible=>scope.Presenter.IsVisible;
        public bool IsActive=>scope.Presenter.IsActive;
        public ITranslateWindowOperation? TryBeginOperation(CancellationToken cancellationToken=default)
        {var op=scope.Presenter.TryBeginOperation(cancellationToken);if(op!=null)scope.Operations.Add(op);return op;}
        public Task<bool> TryShowAsync(ITranslateWindowOperation operation,TranslateWindowRequest request)
        {Requests.Enqueue((operation.Id,request));return scope.Show(operation,request);}
        public Task<bool> TryNotifyAsync(ITranslateWindowOperation operation,NotificationLevel level,Iris.Core.Errors.ErrorCode code)=>scope.Presenter.TryNotifyAsync(operation,level,code);
        public Task HideAsync()=>scope.Presenter.HideAsync();
    }
}
