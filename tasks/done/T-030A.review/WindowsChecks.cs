using System.IO;
using Iris.Core.DependencyInjection;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Graphics.Imaging;

static class WindowsChecks
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(15);
    internal static async Task RunAsync(bool cleanupFailure)
    {
        var boundary=new OcrBoundary();
        var settings=new ReviewSettings(new(){Ocr=new(){Providers=Array.AsReadOnly(new[]{new OcrProviderProfile{Id="actual-local",Kind=OcrProviderKind.WindowsLocal,DisplayName="local",TimeoutSeconds=120,WindowsOptions=new(){LanguageTag="en-US"}}}),ActiveProviderId="actual-local",FallbackProviderId=null}});
        var services=new ServiceCollection();services.AddSingleton<ISettingsService>(settings);services.AddSingleton<ISecretStore>(new ReviewSecrets());
        services.AddSingleton(typeof(ILogger<>),typeof(NullLogger<>));services.AddSingleton<IOcrProviderFactory>(new WindowsOcrProviderFactory(boundary,boundary,TimeProvider.System));services.AddIrisCore();
        await using var container=services.BuildServiceProvider();var registry=container.GetRequiredService<IOcrProviderRegistry>();
        IOcrProviderSnapshot? lease=null;Task<OcrResult>? recognize=null;Task? stop=null;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.BeforeRecognition=async bitmap=>{entered.TrySetResult();await release.Task;_ = bitmap.PixelWidth;};
        if(cleanupFailure)boundary.Failure=stage=>stage=="bitmap-release" ? new InvalidOperationException("controlled bitmap failure") : null;
        try
        {
            lease=await registry.AcquireSnapshotAsync().WaitAsync(Limit);
            var provider=lease.Providers.Single().Provider;
            var image=new ImageData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","hello-123.png")),ImageFormat.Png,800,180);
            recognize=provider.RecognizeAsync(new(image,"en"),CancellationToken.None);
            await entered.Task.WaitAsync(Limit);
            await settings.UpdateAsync(s=>s with{Ocr=s.Ocr with{Providers=Array.AsReadOnly(Array.Empty<OcrProviderProfile>())}});
            await registry.RefreshAsync().WaitAsync(Limit);
            var owner=boundary.Images.Single();
            var liveBitmap=owner.BitmapReference!.Target as SoftwareBitmap;
            RegistryChecks.Check("REAL_WINDOWS_PROVIDER_RETIRES_WITH_LIVE_BITMAP_"+cleanupFailure,liveBitmap?.PixelWidth==800 && !owner.BitmapReleased && !recognize.IsCompleted);
            if(!cleanupFailure)
            {
                stop=registry.StopAsync();RegistryChecks.Check("REGISTRY_STOP_WAITS_FOR_REAL_OCR_LEASE",!stop.IsCompleted && !owner.BitmapReleased);
                release.TrySetResult();await recognize.WaitAsync(Limit);await lease.DisposeAsync();lease=null;await stop.WaitAsync(Limit);
                RegistryChecks.Check("REAL_WINDOWS_BITMAP_AND_STREAM_RELEASE_BEFORE_STOP",owner.BitmapReleased && owner.StreamReleased && owner.WriterReleased);
            }
            else
            {
                release.TrySetResult();Exception? primary=null;try{await recognize.WaitAsync(Limit);}catch(Exception ex){primary=ex;}
                Exception? retiring=null;try{await lease.DisposeAsync();}catch(Exception ex){retiring=ex;}
                RegistryChecks.Check("REGISTRY_RETAINS_REAL_PROVIDER_AFTER_BITMAP_CLEANUP_FAILURE",primary!=null && retiring!=null && !owner.BitmapReleased);
                boundary.Failure=null;await registry.RetryCleanupAsync().WaitAsync(Limit);await lease.DisposeAsync();lease=null;
                RegistryChecks.Check("REGISTRY_RETRY_RELEASES_SAME_REAL_WINDOWS_IMAGE",boundary.Images.Count==1 && owner.BitmapReleased && owner.StreamReleased && owner.BitmapReleaseCalls>=2);
            }
            GC.KeepAlive(liveBitmap);
        }
        finally
        {
            boundary.Failure=null;release.TrySetResult();
            if(recognize!=null)try{await recognize.WaitAsync(Limit);}catch(Exception){}
            var failures=new List<Exception>();
            if(lease!=null)try{await lease.DisposeAsync();}catch(Exception ex){failures.Add(ex);}
            try{await registry.RetryCleanupAsync().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
            try{await registry.StopAsync().WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
            if(stop!=null)try{await stop.WaitAsync(Limit);}catch(Exception ex){failures.Add(ex);}
            foreach(var owner in boundary.Images)try{await owner.DisposeAsync();}catch(Exception ex){failures.Add(ex);}
            if(failures.Count!=0)throw new AggregateException("Actual Windows scope cleanup failed.",failures);
        }
    }
}
