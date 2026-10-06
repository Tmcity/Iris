using System.Runtime.CompilerServices;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.Time.Testing;
using static ProviderScope;

internal static class ResultChecks
{
    internal static async Task Run(string directory, Func<string,Func<Task>,Task> check)
    {
        foreach (uint maximum in new[] { 40U, 10000U, uint.MaxValue })
            await check("CAPABILITY_CLAMP_" + maximum, () => ProviderScope.Run(f =>
            {
                Require(f.Provider.Constraints.MaxLongSide == Math.Min(maximum,32768U), "capability was guessed or overflowed");
                return Task.CompletedTask;
            }, maximum: maximum));
        foreach (uint maximum in new[] { 0U,39U })
            await check("INVALID_CAPABILITY_" + maximum, () =>
            {
                var boundary = new OcrBoundary { Maximum = maximum };
                var factory = new WindowsOcrProviderFactory(boundary,boundary,new FakeTimeProvider());
                bool rejected = false;
                try { factory.Create(new() { Id="bad-capability",DisplayName="Review",Kind=OcrProviderKind.WindowsLocal },ProviderCredentials.None); }
                catch (IrisException ex) { rejected = ex.Code == ErrorCode.ProviderUnavailable; }
                Require(rejected && boundary.Images.IsEmpty && boundary.Engines.IsEmpty, "invalid capability fabricated a configured provider");
                return Task.CompletedTask;
            });
        var bad = new (string Name, IReadOnlyList<WindowsOcrNativeLine>? Lines)[]
        {
            ("null-list",null), ("null-text",[new(null,[])]), ("null-words",[new("x",null)]),
            ("nan",[new("x",[new(double.NaN,0,1,1)])]),
            ("negative",[new("x",[new(-1,0,1,1)])]),
            ("overflow",[new("x",[new(int.MaxValue,0,1,1)])]),
            ("zero-size",[new("x",[new(0,0,0,1)])])
        };
        foreach (var (name,lines) in bad)
            await check("INVALID_NATIVE_RESULT_" + name, () => ProviderScope.Run(async f =>
            {
                f.Boundary.Lines = lines!;
                var failure = await LifecycleChecks.Failure(f.Track(f.Provider.RecognizeAsync(new(await Input(directory),"en"),default),typeof(IrisException)));
                Require(failure.Code == ErrorCode.InvalidResponse && f.AllReleased, "malformed native result was accepted or leaked resources");
            }));
        await check("EMPTY_RESULT_REMAINS_SUCCESS", () => ProviderScope.Run(async f =>
        {
            f.Boundary.Lines = [];
            var result = await f.Track(f.Provider.RecognizeAsync(new(await Input(directory),"en"),default));
            Require(result.Lines.Count == 0 && f.AllReleased, "empty local OCR result became a failure");
        }));
        await check("LIVE_PROVIDER_RELEASES_INPUT_RESULT_BITMAP_AND_ENGINE_REFERENCES", () => ProviderScope.Run(async f =>
        {
            var weak = await CreateWeak(f.Provider,directory);
            var extra = f.Boundary.Engines.Concat(f.Boundary.Images.Select(i => i.BitmapReference!).Where(w => w != null)).ToArray();
            f.Boundary.Lines = [];
            var control = Control();
            for (int i=0;i<12 && (weak.Concat(extra).Any(w=>w.IsAlive) || control.IsAlive);i++)
            {
                await Task.Yield(); GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,true,true);
                GC.WaitForPendingFinalizers(); GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,true,true);
            }
            Console.WriteLine($"LOCAL_OCR_GC inputsResultsAlive={weak.Count(w=>w.IsAlive)} enginesBitmapsAlive={extra.Count(w=>w.IsAlive)} control={control.IsAlive}");
            Require(!control.IsAlive,"GC control failed; product retention is unproven");
            Require(weak.Concat(extra).All(w=>!w.IsAlive),"live provider retained completed input/result/engine/bitmap");
            GC.KeepAlive(f.Provider);
        }));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference Control() => new(new byte[1024]);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CreateWeak(IOcrProvider provider,string directory)
    {
        ImageData? image = await Input(directory);
        OcrResult? result = await provider.RecognizeAsync(new(image,"en"),default);
        WeakReference[] weak = [new(image),new(image.Bytes),new(result),new(result.Lines)];
        image=null;result=null;return weak;
    }
}
