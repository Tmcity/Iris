using System.Collections.Concurrent;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;

// External boundaries only. No Registry publication/reference-counting implementation here.
sealed class ReviewSettings : ISettingsService
{
    private AppSettings _current;
    public int Reads;
    public int Updates;
    public Action? BeforeRead { get; set; }
    public ReviewSettings(AppSettings initial) => _current = initial;
    public AppSettings Current
    {
        get { Interlocked.Increment(ref Reads); BeforeRead?.Invoke(); return Volatile.Read(ref _current); }
    }
    public event EventHandler<SettingsChangedEventArgs>? Changed;
    public Task LoadAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var old = Volatile.Read(ref _current);
        var next = mutate(old);
        Volatile.Write(ref _current, next);
        Interlocked.Increment(ref Updates);
        Changed?.Invoke(this, new(old, next));
        return Task.CompletedTask;
    }
}

sealed class ReviewSecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    public readonly ConcurrentQueue<string> ReadKeys = new();
    public Action<string>? BeforeRead { get; set; }
    public event EventHandler<string>? Changed;
    public string? Get(string key)
    {
        ReadKeys.Enqueue(key);
        BeforeRead?.Invoke(key);
        return _values.GetValueOrDefault(key);
    }
    public void Set(string key, string value) { _values[key] = value; Changed?.Invoke(this,key); }
    public bool Remove(string key)
    {
        var removed = _values.TryRemove(key, out _);
        if (removed) Changed?.Invoke(this,key);
        return removed;
    }
}

sealed class ReviewFactory : IOcrProviderFactory
{
    public OcrProviderKind Kind { get; }
    public ReviewFactory(OcrProviderKind kind = OcrProviderKind.WindowsLocal) => Kind = kind;
    public readonly ConcurrentQueue<ReviewProvider> Created = new();
    public readonly ConcurrentQueue<(OcrProviderProfile Profile, ProviderCredentials Credentials)> Inputs = new();
    public Action<OcrProviderProfile>? BeforeCreate { get; set; }
    public Action<ReviewProvider>? Configure { get; set; }
    public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials)
    {
        BeforeCreate?.Invoke(profile);
        Inputs.Enqueue((profile, credentials));
        var instance = new ReviewProvider(profile);
        Configure?.Invoke(instance);
        Created.Enqueue(instance);
        return instance;
    }
    // Remove boundary-held roots before asserting that product ownership was released.
    public void ForgetCreated() { while (Created.TryDequeue(out _)) { } while (Inputs.TryDequeue(out _)) { } }
}

sealed class ReviewProvider : IOcrProvider, IAsyncDisposable, IDisposable
{
    public ProviderDescriptor Descriptor { get; }
    public ImageConstraints Constraints { get; } = new(1048576, false, 1000, 0, Array.AsReadOnly(new[] { ImageFormat.Png }));
    public readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? DisposeRelease { get; set; }
    public int AsyncDisposals;
    public int SyncDisposals;
    public int ActiveDisposals;
    public int PeakDisposals;
    public int FailuresRemaining;
    public bool Disposed;
    public const string FakeSecret = "FAKE_REVIEW_SECRET_MUST_NOT_ESCAPE";
    public ReviewProvider(OcrProviderProfile profile) => Descriptor = new(profile.Id,profile.DisplayName,profile.Kind.ToString(),null,true);
    public Task<ProviderTestResult> TestAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed,this);
        return Task.FromResult(new ProviderTestResult(true,TimeSpan.Zero));
    }
    public Task<OcrResult> RecognizeAsync(OcrRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed,this);
        return Task.FromResult(new OcrResult(Descriptor.Id,Array.Empty<OcrLine>(),null,TimeSpan.Zero));
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref AsyncDisposals);
        var active = Interlocked.Increment(ref ActiveDisposals);
        int before;
        do { before = PeakDisposals; if(before >= active) break; }
        while(Interlocked.CompareExchange(ref PeakDisposals,active,before)!=before);
        DisposeEntered.TrySetResult();
        try
        {
            if(DisposeRelease is { } gate) await gate.Task.ConfigureAwait(false);
            if(Interlocked.Decrement(ref FailuresRemaining)>=0) throw new InvalidOperationException(FakeSecret);
            Disposed=true;
        }
        finally { Interlocked.Decrement(ref ActiveDisposals); }
    }
    public void Dispose() { Interlocked.Increment(ref SyncDisposals); throw new InvalidOperationException("Registry must prefer actual asynchronous ownership."); }
}

sealed class FactoryGate : IDisposable
{
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(false);
    public void Hold(OcrProviderProfile _)
    {
        Entered.TrySetResult();
        _release.Wait();
    }
    public void Release() => _release.Set();
    // Caller must release and join Factory before disposing this primitive.
    public void Dispose() => _release.Dispose();
}
