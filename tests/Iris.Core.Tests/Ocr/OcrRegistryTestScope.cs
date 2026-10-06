using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Core.Tests.Ocr;

internal sealed class OcrRegistryTestScope
{
    internal readonly Store Settings = new();
    internal readonly Secrets SecretStore = new();
    internal readonly Factory Providers = new();
    internal OcrProviderRegistry Registry { get; }
    internal readonly List<(Task Task, Type? Expected)> Work = [];
    internal readonly ConcurrentBag<IOcrProviderSnapshot> Snapshots = [];
    internal readonly List<Action> Releases = [];
    internal OcrRegistryTestScope() => Registry = new(Settings, SecretStore, [Providers], NullLogger<OcrProviderRegistry>.Instance);
    internal static OcrProviderProfile Profile(string id = "local", string name = "Local") => new() { Id = id, DisplayName = name, Kind = OcrProviderKind.WindowsLocal };
    internal static AppSettings Config(params OcrProviderProfile[] profiles) => new() { Ocr = new() { Providers = profiles } };
    internal Task Refresh(Type? expected = null, CancellationToken token = default) => Track(Registry.RefreshAsync(token), expected);
    internal Task<IOcrProviderSnapshot> Acquire(Type? expected = null, CancellationToken token = default) => Track(AcquireCore(token), expected);
    private async Task<IOcrProviderSnapshot> AcquireCore(CancellationToken token)
    { var snapshot = await Registry.AcquireSnapshotAsync(token); Snapshots.Add(snapshot); return snapshot; }
    internal Task<IOcrProviderSnapshot> Transient(OcrProviderProfile profile, ProviderCredentials credentials, Type? expected = null, CancellationToken token = default) => Track(TransientCore(profile, credentials, token), expected);
    private async Task<IOcrProviderSnapshot> TransientCore(OcrProviderProfile profile, ProviderCredentials credentials, CancellationToken token)
    { var snapshot = await Registry.CreateTransientAsync(profile, credentials, token); Snapshots.Add(snapshot); return snapshot; }
    internal T Track<T>(T task, Type? expected = null) where T : Task { Work.Add((task, expected)); return task; }
    internal static async Task Run(Func<OcrRegistryTestScope, Task> body)
    {
        var f = new OcrRegistryTestScope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Releases) release();
            f.Settings.BeforeRead = null; f.SecretStore.BeforeGet = null; f.Providers.BeforeCreate = null; f.Providers.Throw = false;
            foreach (var p in f.Providers.Created) { p.FailDispose = false; p.FailDescriptor = false; }
            foreach (var item in f.Work)
                try { await item.Task; } catch (Exception ex) { if (item.Expected == null || !item.Expected.IsInstanceOfType(ex)) errors.Add(ex); }
            foreach (var snapshot in f.Snapshots)
                try { await snapshot.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Registry.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (f.Providers.Created.Any(p => !p.Disposed)) errors.Add(new InvalidOperationException("Unreleased provider."));
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    internal sealed class Store : ISettingsService
    {
        internal AppSettings Value = Config();
        internal int Reads, Updates, Subscriptions;
        internal Action? BeforeRead { get; set; }
        internal Func<AppSettings, AppSettings>? OnUpdate { get; set; }
        private EventHandler<SettingsChangedEventArgs>? _changed;
        public AppSettings Current { get { Interlocked.Increment(ref Reads); BeforeRead?.Invoke(); return Value; } }
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { _changed += value; Subscriptions++; } remove { _changed -= value; Subscriptions--; } }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Updates++; var old = Value; var next = mutate(old); Value = OnUpdate?.Invoke(next) ?? next; _changed?.Invoke(this, new(old, Value)); return Task.CompletedTask; }
    }
    internal sealed class Secrets : ISecretStore
    {
        internal readonly ConcurrentDictionary<string, string> Values = new();
        internal readonly ConcurrentQueue<string> Reads = new();
        internal readonly ConcurrentQueue<string> Removed = new();
        internal Action<string>? BeforeGet { get; set; }
        internal int Subscriptions;
        internal int Sets;
        private EventHandler<string>? _changed;
        public event EventHandler<string>? Changed { add { _changed += value; Subscriptions++; } remove { _changed -= value; Subscriptions--; } }
        public string? Get(string key) { Reads.Enqueue(key); BeforeGet?.Invoke(key); return Values.TryGetValue(key, out var value) ? value : null; }
        public void Set(string key, string value) { Sets++; Values[key] = value; _changed?.Invoke(this, key); }
        public bool Remove(string key) { Removed.Enqueue(key); bool result = Values.TryRemove(key, out _); if (result) _changed?.Invoke(this, key); return result; }
    }
    internal sealed class Factory : IOcrProviderFactory
    {
        public OcrProviderKind Kind => OcrProviderKind.WindowsLocal;
        internal readonly ConcurrentQueue<Provider> Created = new();
        internal Action<OcrProviderProfile>? BeforeCreate { get; set; }
        internal bool Throw { get; set; }
        internal int CreateCalls;
        internal Action<Provider>? Configure { get; set; }
        public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials)
        {
            Interlocked.Increment(ref CreateCalls); BeforeCreate?.Invoke(profile); if (Throw) throw new IOException("private credentials factory");
            var provider = new Provider(profile, credentials); Configure?.Invoke(provider); Created.Enqueue(provider); return provider;
        }
    }
    internal sealed class Provider(OcrProviderProfile profile, ProviderCredentials credentials) : IOcrProvider, IDisposable, IAsyncDisposable
    {
        internal OcrProviderProfile Profile { get; } = profile;
        internal ProviderCredentials Credentials { get; } = credentials;
        internal bool Disposed, FailDispose, FailDescriptor;
        internal int DisposeCalls, SyncCalls;
        internal Func<Task>? BeforeDispose { get; set; }
        internal Action? BeforeDescriptor { get; set; }
        internal Func<OcrRequest, CancellationToken, Task<OcrResult>>? Recognition { get; set; }
        public ProviderDescriptor Descriptor { get { BeforeDescriptor?.Invoke(); return FailDescriptor ? throw new IOException("private descriptor") : new(Profile.Id, Profile.DisplayName, "WindowsLocal", null, true); } }
        public ImageConstraints Constraints { get; } = new(100000, false, 32768, 40, [ImageFormat.Png]);
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        { ObjectDisposedException.ThrowIf(Disposed, this); cancellationToken.ThrowIfCancellationRequested(); return Recognition?.Invoke(request, cancellationToken) ?? Task.FromResult(new OcrResult(Profile.Id, [], null, TimeSpan.Zero)); }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
        { ObjectDisposedException.ThrowIf(Disposed, this); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero)); }
        public async ValueTask DisposeAsync()
        { Interlocked.Increment(ref DisposeCalls); if (BeforeDispose != null) await BeforeDispose(); if (FailDispose) throw new IOException("private dispose"); Disposed = true; }
        public void Dispose() { SyncCalls++; throw new InvalidOperationException("Must prefer async disposal."); }
    }
}
