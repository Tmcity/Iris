using System.Runtime.CompilerServices;
using Iris.Core.Imaging;

static class OwnershipChecks
{
    internal static async Task RunAsync()
    {
        await using var scope=new PipelineScope();
        var weak=await Complete(scope);
        scope.Main.Requests.Clear();scope.Backup.Requests.Clear();scope.Preprocessor.Calls.Clear();scope.Registry.Issued.Clear();
        var control=new WeakReference(scope.Service);
        for(int i=0;i<5;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();await Task.Yield();}
        PipelineScope.Check("LIVE_SERVICE_RETAINS_NO_INPUT_RESULT_OR_SNAPSHOT",weak.All(x=>!x.IsAlive) && control.IsAlive);
        Console.WriteLine("GC_REFERENCE_FLAGS="+string.Join(',',weak.Select(x=>x.IsAlive)));
        GC.KeepAlive(scope.Service);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<WeakReference[]> Complete(PipelineScope scope)
    {
        ImageData? image=new(new byte[4096],ImageFormat.Png,40,40);
        var result=await scope.Service.RecognizeAsync(image);
        var refs=new[]{new WeakReference(image),new WeakReference(image.Bytes),new WeakReference(result),new WeakReference(result.Raw),new WeakReference(scope.Registry.Issued.Single()),new WeakReference(new byte[100])};
        image=null;result=null!;return refs;
    }
}
