using System.Reflection;
using System.Runtime.CompilerServices;
using Iris.Core.Settings;

static class ResourceChecks
{
    internal static async Task Run()
    {
        await using var scope=new ReviewScope();var (session,references)=await CloseAndForget(scope);
        scope.Cloud.Created.Clear();scope.Local.Created.Clear();
        for(int i=0;i<7;i++){await Task.Yield();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Console.WriteLine("CLOSED_DRAFT_REFERENCES provider,credentials,capturedDraft,control="+string.Join(',',references.Select(r=>r.IsAlive)));
        if(references.Any(r=>r.IsAlive))throw new InvalidOperationException("Closed draft session retained resources while session stayed alive.");
        GC.KeepAlive(session);Console.WriteLine("DRAFT_CLOSED_RESOURCE_RELEASE=PASS");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<(IOcrDraftTestSession,WeakReference[])> CloseAndForget(ReviewScope scope)
    {
        var baseline=scope.Settings.Current.Ocr.Providers[0];
        var draft=new OcrProviderDraft(baseline with{DisplayName="Synthetic "+Guid.NewGuid().ToString("N")},baseline,new(CredentialEditKind.Replace,new(new string('k',64),new string('s',64))));
        var session=scope.Editor.CreateOcrDraftTest(draft);scope.Sessions.Add(session);await session.RunAsync();
        var provider=scope.Cloud.Created.Single();
        var captured=session.GetType().GetField("_draft",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
        var references=new[]{new WeakReference(provider),new WeakReference(provider.Credentials),new WeakReference(captured),new WeakReference(new byte[64])};
        await session.DisposeAsync();return(session,references);
    }
}
