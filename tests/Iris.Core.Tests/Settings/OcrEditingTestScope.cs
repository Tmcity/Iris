using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.Core.Tests.Settings;

internal sealed class OcrEditingTestScope
{
    internal readonly Store Settings = new();
    internal readonly Secrets SecretStore = new();
    internal readonly Factory Local = new(OcrProviderKind.WindowsLocal);
    internal readonly Factory Cloud = new(OcrProviderKind.BaiduCloud);
    internal readonly Observer OtherObserver = new();
    internal readonly Observer OcrObserver = new();
    internal OcrProviderRegistry Registry { get; }
    internal readonly SettingsEditCoordinator Coordinator;
    internal readonly List<IOcrDraftTestSession> Sessions = [];
    internal readonly List<(Task Work, Type? Failure)> Work = [];
    internal readonly List<Gate> Gates = [];

    internal OcrEditingTestScope()
    {
        Registry = new(Settings, SecretStore, [Local, Cloud], NullLogger<OcrProviderRegistry>.Instance);
        OcrObserver.Body = () => Registry.RefreshAsync();
        Coordinator = new(Settings, SecretStore, Substitute.For<ITranslationProviderRegistry>(), [OtherObserver, OcrObserver],
            Substitute.For<IPromptTemplateProvider>(), TimeProvider.System, NullLogger<SettingsEditCoordinator>.Instance,
            ocrProviderRegistry: Registry);
    }

    internal static OcrProviderProfile Profile(string id = "local", OcrProviderKind kind = OcrProviderKind.WindowsLocal) => new()
    { Id = id, DisplayName = id, Kind = kind, BaiduOptions = kind == OcrProviderKind.BaiduCloud ? new() : null, WindowsOptions = kind == OcrProviderKind.WindowsLocal ? new() : null };
    internal static SettingsEditPlan Plan(params OcrProfileChange[] changes) => new(new(), []) { OcrChanges = changes };
    internal static OcrProfileChange Update(OcrProviderProfile baseline, OcrProviderProfile target, ProviderCredentialEdit? credentials = null) =>
        new(OcrProfileChangeKind.Update, baseline, target, credentials ?? new(CredentialEditKind.Keep));
    internal IOcrDraftTestSession Draft(OcrProviderDraft draft)
    { var session = Coordinator.CreateOcrDraftTest(draft); Sessions.Add(session); return session; }
    internal Gate NewGate() { var g = new Gate(); Gates.Add(g); return g; }
    internal T Track<T>(T task, Type? failure = null) where T : Task { Work.Add((task, failure)); return task; }

    internal static async Task Run(Func<OcrEditingTestScope, Task> body)
    {
        var f = new OcrEditingTestScope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            f.Local.Configure = null; f.Cloud.Configure = null;
            foreach (var gate in f.Gates) gate.Open();
            f.Settings.BeforeMutate = null; f.Settings.BeforeUpdate = null; f.Settings.AfterUpdate = null;
            f.SecretStore.BeforeSet = null; f.SecretStore.BeforeGet = null; f.SecretStore.BeforeRemove = null;
            f.Local.BeforeCreate = null; f.Cloud.BeforeCreate = null;
            foreach (var p in f.Local.Created.Concat(f.Cloud.Created)) p.FailDispose = false;
            foreach (var (work, failure) in f.Work)
                try { await work; } catch (Exception ex) { if (failure == null || !failure.IsInstanceOfType(ex)) errors.Add(ex); }
            foreach (var session in f.Sessions)
                try { await session.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Registry.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var gate in f.Gates) gate.Dispose();
            if (f.Local.Created.Concat(f.Cloud.Created).Any(p => !p.Disposed)) errors.Add(new InvalidOperationException("Owned provider was not disposed."));
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    internal sealed class Gate : IDisposable
    {
        private readonly ManualResetEventSlim _sync = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task PauseAsync() { Entered.TrySetResult(); return _release.Task; }
        internal void Pause() { Entered.TrySetResult(); _sync.Wait(); }
        internal void Open() { _sync.Set(); _release.TrySetResult(); }
        public void Dispose() => _sync.Dispose();
    }

    internal sealed class Store : ISettingsService
    {
        internal AppSettings Value = new();
        internal int Reads, Updates;
        internal Action? BeforeMutate, AfterUpdate;
        internal Func<Task>? BeforeUpdate;
        public AppSettings Current { get { Interlocked.Increment(ref Reads); return Value; } }
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            if (BeforeUpdate != null) await BeforeUpdate();
            cancellationToken.ThrowIfCancellationRequested(); BeforeMutate?.Invoke();
            var old = Value; var next = mutate(old); Updates++; Value = next;
            Changed?.Invoke(this, new(old, next)); AfterUpdate?.Invoke();
        }
    }

    internal sealed class Secrets : ISecretStore
    {
        internal readonly ConcurrentDictionary<string, string> Values = new();
        internal readonly ConcurrentQueue<string> Reads = new(), Writes = new(), Removes = new();
        internal Action<string>? BeforeGet, BeforeSet, BeforeRemove;
        public event EventHandler<string>? Changed;
        public string? Get(string key) { Reads.Enqueue(key); BeforeGet?.Invoke(key); return Values.GetValueOrDefault(key); }
        public void Set(string key, string value) { Writes.Enqueue(key); BeforeSet?.Invoke(key); Values[key] = value; Changed?.Invoke(this, key); }
        public bool Remove(string key) { Removes.Enqueue(key); BeforeRemove?.Invoke(key); var removed = Values.TryRemove(key, out _); if (removed) Changed?.Invoke(this, key); return removed; }
    }

    internal sealed class Observer : IProviderConfigurationObserver
    {
        internal bool Fail;
        internal int Calls;
        internal Func<Task>? Body;
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        { Calls++; if (Fail) throw new IOException("synthetic private observer"); if (Body != null) await Body(); }
    }

    internal sealed class Factory(OcrProviderKind kind) : IOcrProviderFactory
    {
        public OcrProviderKind Kind => kind;
        internal readonly ConcurrentQueue<Provider> Created = new();
        internal Action? BeforeCreate;
        internal Action<Provider>? Configure { get; set; }
        public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials)
        { BeforeCreate?.Invoke(); var p = new Provider(profile, credentials); Configure?.Invoke(p); Created.Enqueue(p); return p; }
    }

    internal sealed class Provider(OcrProviderProfile profile, ProviderCredentials credentials) : IOcrProvider, IAsyncDisposable
    {
        internal readonly OcrProviderProfile Profile = profile;
        internal readonly ProviderCredentials Credentials = credentials;
        internal bool Disposed, FailDispose;
        internal int TestCalls, RecognitionCalls, DisposeCalls;
        internal Func<CancellationToken, Task<ProviderTestResult>>? Test { get; set; }
        internal Func<Task>? BeforeDispose { get; set; }
        public ProviderDescriptor Descriptor => new(Profile.Id, Profile.DisplayName, Profile.Kind.ToString(), null, true);
        public ImageConstraints Constraints { get; } = new(1000, false, 100, 40, [ImageFormat.Png]);
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        { RecognitionCalls++; throw new InvalidOperationException("Draft must not recognize."); }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken)
        { TestCalls++; return Test?.Invoke(cancellationToken) ?? Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero)); }
        public async ValueTask DisposeAsync()
        { DisposeCalls++; if (BeforeDispose != null) await BeforeDispose(); if (FailDispose) throw new IOException("synthetic private cleanup"); Disposed = true; }
    }
}
