using Iris.Core.Presentation;
using Iris.Core.Translation;

static class PublicationChecks
{
    internal static async Task<bool> CallerCancellation(ReviewSta sta)
    {
        await using var scope=new PresentationScope(sta);
        using var caller=new CancellationTokenSource();
        var operation=scope.Begin(caller.Token);
        await scope.Show(operation,new(){StatusMessage="Synthetic OCR loading",Origin=TextOrigin.Screenshot,AutoTranslate=false});
        bool accepted=false;
        using var later=caller.Token.Register(()=>
        {
            accepted=scope.Show(operation,new(){Text="LATE_AFTER_CALLER_CANCEL",Origin=TextOrigin.Screenshot,AutoTranslate=false}).GetAwaiter().GetResult();
        });
        await Task.Run(caller.Cancel);
        var text=await sta.Run(()=>scope.Presenter.ViewModel!.InputText);
        Console.WriteLine($"CALLER_CANCEL accepted={accepted} text={text}");
        return !accepted && text!="LATE_AFTER_CALLER_CANCEL";
    }
}
