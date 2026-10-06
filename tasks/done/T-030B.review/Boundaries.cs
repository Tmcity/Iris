using System.Collections.Concurrent;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Settings;

sealed class PipelineProvider(string id):IOcrProvider
{
    internal bool Configured=true;
    internal Func<OcrRequest,CancellationToken,Task<OcrResult>>? Body {get;set;}
    internal Exception? ConstraintsError {get;set;}
    internal ImageConstraints Limits {get;set;}=new(100000,false,1000,0,Array.AsReadOnly(new[]{ImageFormat.Png}));
    internal readonly ConcurrentQueue<OcrRequest> Requests=new();
    public ProviderDescriptor Descriptor=>new(id,id,"fake",null,Configured);
    public ImageConstraints Constraints=>ConstraintsError==null?Limits:throw ConstraintsError;
    public Task<ProviderTestResult> TestAsync(CancellationToken token)=>throw new NotSupportedException();
    public Task<OcrResult> RecognizeAsync(OcrRequest request,CancellationToken token)
    {
        Requests.Enqueue(request);
        return Body?.Invoke(request,token)??Task.FromResult(new OcrResult(id,Array.AsReadOnly(new[]{new OcrLine(id)}),null,TimeSpan.FromMilliseconds(17)));
    }
}

sealed class PipelinePreprocessor:IImagePreprocessor
{
    internal readonly ConcurrentQueue<(ImageData Image,ImageConstraints Constraints)> Calls=new();
    internal Func<ImageData,ImageConstraints,CancellationToken,Task<ImageData>>? Body {get;set;}
    public Task<ImageData> PrepareAsync(ImageData image,ImageConstraints constraints,CancellationToken token)
    {
        Calls.Enqueue((image,constraints));return Body?.Invoke(image,constraints,token)??Task.FromResult(new ImageData(new byte[]{9,8,7},ImageFormat.Png,40,40));
    }
}

sealed class PipelineSnapshot:IOcrProviderSnapshot
{
    private bool _released;
    internal AppSettings Captured {get;set;}=new();
    internal IReadOnlyList<OcrProviderRegistration> Entries {get;set;}=Array.Empty<OcrProviderRegistration>();
    internal bool FailRelease;
    internal int Releases,ActiveReleases,PeakReleases;
    internal TaskCompletionSource? ReleaseGate {get;set;}
    internal Action? OnReleaseAttempt {get;set;}
    internal readonly TaskCompletionSource ReleaseEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AppSettings Settings {get{ObjectDisposedException.ThrowIf(_released,this);return Captured;}}
    public IReadOnlyList<OcrProviderRegistration> Providers {get{ObjectDisposedException.ThrowIf(_released,this);return Entries;}}
    public async ValueTask DisposeAsync()
    {
        _released=true;Interlocked.Increment(ref Releases);int n=Interlocked.Increment(ref ActiveReleases);PeakReleases=Math.Max(PeakReleases,n);ReleaseEntered.TrySetResult();OnReleaseAttempt?.Invoke();
        try{if(ReleaseGate!=null)await ReleaseGate.Task;if(FailRelease)throw new InvalidOperationException("FAKE_PRIVATE_RELEASE_TEXT");}
        finally{Interlocked.Decrement(ref ActiveReleases);}
    }
}

sealed class PipelineRegistry:IOcrProviderRegistry
{
    internal readonly ConcurrentQueue<PipelineSnapshot> Issued=new();
    internal Func<CancellationToken,Task<PipelineSnapshot>>? Acquire {get;set;}
    internal Func<PipelineSnapshot> Factory {get;set;}=()=>new();
    internal Func<Task>? OnStop {get;set;}
    internal int Stops;
    public event EventHandler? Changed {add{}remove{}}
    public Task RefreshAsync(CancellationToken token=default)=>Task.CompletedTask;
    public async Task<IOcrProviderSnapshot> AcquireSnapshotAsync(CancellationToken token=default)
    {
        var item=Acquire==null?Factory():await Acquire(token);Issued.Enqueue(item);return item;
    }
    public Task<IOcrProviderSnapshot> CreateTransientAsync(OcrProviderProfile profile,ProviderCredentials credentials,CancellationToken token=default)=>throw new NotSupportedException();
    public void StopAccepting(){}
    public Task StopAsync(CancellationToken token=default){Interlocked.Increment(ref Stops);return OnStop?.Invoke()??Task.CompletedTask;}
    public Task RetryCleanupAsync(CancellationToken token=default)=>Task.CompletedTask;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

sealed class PipelineScope:IAsyncDisposable
{
    internal static Func<IOcrProviderRegistry,IImagePreprocessor,IOcrTextNormalizer,IOcrService> Create=null!;
    internal static Func<IOcrTextNormalizer> CreateNormalizer=null!;
    internal static Action<string,bool> Check=null!;
    internal readonly PipelineProvider Main=new("main"),Backup=new("backup");
    internal readonly PipelinePreprocessor Preprocessor=new();
    internal readonly PipelineRegistry Registry=new();
    internal readonly ImageData Original=new(new byte[]{1,2,3},ImageFormat.Png,80,80);
    internal readonly List<Action> Releases=new();
    internal readonly List<Task> Tasks=new();
    internal IOcrService Service {get;}
    internal PipelineScope()
    {
        Registry.Factory=()=>new PipelineSnapshot
        {
            Captured=new(){Ocr=new(){ActiveProviderId="main",FallbackProviderId="backup",LanguageHint="ja",TextLayout=OcrTextLayout.KeepLines}},
            Entries=Array.AsReadOnly(new[]{Entry(Main),Entry(Backup)})
        };
        Service=Create(Registry,Preprocessor,CreateNormalizer());
    }
    static OcrProviderRegistration Entry(PipelineProvider provider)=>new(new(){Id=provider.Descriptor.Id,DisplayName=provider.Descriptor.Id,Kind=OcrProviderKind.WindowsLocal},provider,Guid.NewGuid());
    internal Task<OcrOutcome> Run(CancellationToken token=default,string? id=null){var task=Service.RecognizeAsync(Original,id,token);Tasks.Add(task);return task;}
    public async ValueTask DisposeAsync()
    {
        foreach(var release in Releases)release();
        foreach(var item in Registry.Issued){item.FailRelease=false;item.ReleaseGate?.TrySetResult();}
        // Task outcome belongs to each check; all tasks must still be observed before releasing the service.
        foreach(var task in Tasks)try{await task.WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception){}
        foreach(var item in Registry.Issued){item.FailRelease=false;item.ReleaseGate?.TrySetResult();}
        var errors=new List<Exception>();
        try{await Service.RetryCleanupAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception ex){errors.Add(ex);}
        try{await Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception ex){errors.Add(ex);}
        if(errors.Count!=0)throw new AggregateException("Pipeline scope cleanup failed.",errors);
    }
}
