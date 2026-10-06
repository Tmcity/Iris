using Iris.Core.Presentation;
using Iris.Core.Translation;

await using var sta=new ReviewSta();
if(args.Length>0 && args[0]=="dispatcher")
{
    await EntryChecks.RunExclusivity(sta);return 0;
}
if(args.Length>0 && args[0]=="host")
{
    await HostChecks.Run(sta);return 0;
}
if(args.Length>0 && args[0]=="effects")
{
    await EffectChecks.Run(sta);return 0;
}
if(args.Length>0 && args[0]=="entries")
{
    await EntryChecks.Run(sta);return 0;
}
if(args.Length>0 && args[0]=="caller-cancel")
{
    bool passed=await PublicationChecks.CallerCancellation(sta);
    Console.WriteLine($"ORIGINAL_CALLER_CANCEL_ACCEPTANCE={(passed?"PASS":"FAIL")}");
    return passed?0:1;
}
await using(var scope=new PresentationScope(sta))
{
    var operation=scope.Begin();
    bool accepted=await scope.Show(operation,new(){Text="Synthetic OCR baseline",Origin=TextOrigin.Screenshot,AutoTranslate=false});
    await scope.Presenter.ViewModel!.WaitForActiveWorkAsync();
    var text=await sta.Run(()=>scope.Presenter.ViewModel!.InputText);
    if(!accepted || text!="Synthetic OCR baseline" || !scope.Translation.Requests.IsEmpty)
        throw new InvalidOperationException("Baseline original-text publication did not match existing contract.");
    Console.WriteLine("HARNESS_BASELINE_REAL_TRANSLATION_WINDOW_TEXT_ONLY=PASS");
}
Console.WriteLine("HARNESS_VALIDATION_ONLY: T-031B handlers not tested yet; owned windows closed.");
return 0;
