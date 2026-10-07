using System.Collections.Concurrent;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;

sealed class MemorySettings(AppSettings initial):ISettingsService
{
    private readonly object gate=new();
    private AppSettings current=ReviewFreeze.Settings(initial);
    internal int Reads,Updates;
    internal Func<CancellationToken,Task>? BeforeUpdate {get;set;}
    internal Func<AppSettings,AppSettings>? BeforeMutate {get;set;}
    internal Action? AfterCommit {get;set;}
    public AppSettings Current {get{Interlocked.Increment(ref Reads);lock(gate)return current;}}
    public event EventHandler<SettingsChangedEventArgs>? Changed;
    public Task LoadAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public async Task UpdateAsync(Func<AppSettings,AppSettings> mutate,CancellationToken cancellationToken=default)
    {
        if(BeforeUpdate!=null)await BeforeUpdate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock(gate)
        {
            if(BeforeMutate!=null)current=ReviewFreeze.Settings(BeforeMutate(current));
            var old=current;var next=ReviewFreeze.Settings(mutate(current));
            Interlocked.Increment(ref Updates);current=next;Changed?.Invoke(this,new(old,next));
        }
        AfterCommit?.Invoke();
    }
    internal void ExternalChange(Func<AppSettings,AppSettings> mutate)
    {lock(gate){var old=current;current=ReviewFreeze.Settings(mutate(current));Changed?.Invoke(this,new(old,current));}}
}

sealed class MemorySecrets:ISecretStore
{
    private readonly object gate=new();
    private readonly Dictionary<string,string> values=new();
    internal int Gets,Sets,Removes;
    internal Action<string>? BeforeGet {get;set;}
    internal Action<string>? BeforeSet {get;set;}
    internal Action<string>? BeforeRemove {get;set;}
    public event EventHandler<string>? Changed;
    public string? Get(string key){Interlocked.Increment(ref Gets);BeforeGet?.Invoke(key);lock(gate)return values.GetValueOrDefault(key);}
    public void Set(string key,string value)
    {BeforeSet?.Invoke(key);lock(gate){values[key]=value;Sets++;}Changed?.Invoke(this,key);}
    public bool Remove(string key)
    {BeforeRemove?.Invoke(key);bool removed;lock(gate){removed=values.Remove(key);Removes++;}if(removed)Changed?.Invoke(this,key);return removed;}
    internal IReadOnlyDictionary<string,string> Snapshot(){lock(gate)return new Dictionary<string,string>(values);}
}

sealed class TestOcrProvider(OcrProviderProfile profile,ProviderCredentials credentials):IOcrProvider,IAsyncDisposable
{
    public ProviderDescriptor Descriptor {get;}=new(profile.Id,profile.DisplayName,profile.Kind.ToString(),null,true);
    public Iris.Core.Imaging.ImageConstraints Constraints {get;}=new(100000,false,2048,0,[Iris.Core.Imaging.ImageFormat.Png]);
    internal ProviderCredentials Credentials {get;}=credentials;
    internal int TestCalls,RecognizeCalls,DisposeCalls;
    internal bool FailDispose {get;set;}
    internal Func<CancellationToken,Task<ProviderTestResult>> Body {get;set;}=_=>Task.FromResult(new ProviderTestResult(true,TimeSpan.Zero));
    internal Func<Task>? BeforeDispose {get;set;}
    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken){Interlocked.Increment(ref TestCalls);return Body(cancellationToken);}
    public Task<OcrResult> RecognizeAsync(OcrRequest request,CancellationToken cancellationToken)
    {Interlocked.Increment(ref RecognizeCalls);throw new InvalidOperationException("Draft test must not recognize an image.");}
    public async ValueTask DisposeAsync()
    {Interlocked.Increment(ref DisposeCalls);if(BeforeDispose!=null)await BeforeDispose();if(FailDispose)throw new InvalidOperationException("Controlled provider cleanup failure.");}
}

sealed class TestOcrFactory(OcrProviderKind kind):IOcrProviderFactory
{
    public OcrProviderKind Kind=>kind;
    internal readonly ConcurrentQueue<TestOcrProvider> Created=new();
    internal Action<TestOcrProvider>? Configure {get;set;}
    public IOcrProvider Create(OcrProviderProfile profile,ProviderCredentials credentials)
    {var provider=new TestOcrProvider(profile,credentials);Configure?.Invoke(provider);Created.Enqueue(provider);return provider;}
}

static class ReviewFreeze
{
    internal static AppSettings Settings(AppSettings value) => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(value))!;
}
