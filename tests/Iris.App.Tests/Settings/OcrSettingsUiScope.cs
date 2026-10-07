using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Windows;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Iris.App.Tests.Settings;

internal sealed class OcrSettingsUiScope
{
    internal readonly Store Settings = new();
    internal readonly Secrets SecretStore = new();
    internal readonly OcrFactory Cloud = new(OcrProviderKind.BaiduCloud), Local = new(OcrProviderKind.WindowsLocal);
    internal readonly TranslationFactory Translation = new();
    internal readonly Observer ExtraObserver = new();
    internal readonly PausableUi Ui = new();
    internal readonly List<Gate> Gates = [];
    internal readonly List<(Task Task, Type? Error)> Tasks = [];
    internal ServiceProvider Services { get; }
    internal IServiceScope Scope { get; }
    internal SettingsViewModel Vm { get; }
    internal readonly SettingsWindow Window;
    internal SettingsWindowSession Session { get; }
    internal readonly SettingsApplyReceiptManager Receipts;
    internal IOcrProviderRegistry Registry { get; }

    internal OcrSettingsUiScope(AppSettings? initial = null)
    {
        if (initial != null) Settings.Value = initial;
        var services = new ServiceCollection(); services.AddLogging(); services.AddIrisCore();
        services.AddSingleton<ISettingsService>(Settings); services.AddSingleton<ISecretStore>(SecretStore);
        services.AddSingleton<IOcrProviderFactory>(Cloud); services.AddSingleton<IOcrProviderFactory>(Local);
        services.AddSingleton<ITranslationProviderFactory>(Translation); services.AddSingleton<IProviderConfigurationObserver>(ExtraObserver);
        services.AddSingleton(Substitute.For<IAutoStartService>()); services.AddSingleton(Substitute.For<ITranslationHistoryRepository>());
        services.AddSingleton<IUiDispatcher>(Ui); services.AddSingleton<ThemeService>();
        services.AddSingleton<SettingsApplyReceiptManager>(); services.AddSingleton<ISettingsApplyReceiptManager>(s => s.GetRequiredService<SettingsApplyReceiptManager>());
        services.AddScoped<GeneralSettingsSaveCoordinator>(); services.AddScoped<SettingsViewModel>();
        services.AddTransient(_ => new SettingsWindow { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 });
        Services = services.BuildServiceProvider(); Scope = Services.CreateScope();
        Vm = Scope.ServiceProvider.GetRequiredService<SettingsViewModel>(); Window = Scope.ServiceProvider.GetRequiredService<SettingsWindow>(); Window.DataContext = Vm;
        Session = new(Scope, Window, Vm, Services.GetRequiredService<ThemeService>(), Scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>());
        Receipts = Services.GetRequiredService<SettingsApplyReceiptManager>(); Registry = Services.GetRequiredService<IOcrProviderRegistry>();
    }
    internal void Show(string page = "ocr") { Vm.CurrentPageKey = page; Window.Show(); Window.UpdateLayout(); }
    internal Gate NewGate() { var gate = new Gate(); Gates.Add(gate); return gate; }
    internal T Track<T>(T task, Type? error = null) where T : Task { Tasks.Add((task, error)); return task; }
    internal static Task Run(Func<OcrSettingsUiScope, Task> body, AppSettings? initial = null) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new OcrSettingsUiScope(initial); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            f.Cloud.Configure = null; f.Local.Configure = null;
            foreach (var gate in f.Gates) gate.Open();
            f.Settings.BeforeUpdate = null; f.Settings.Failure = null; f.ExtraObserver.Fail = false;
            foreach (var p in f.Cloud.Created.Concat(f.Local.Created)) p.FailDispose = false;
            foreach (var (task, error) in f.Tasks)
                try { await task; } catch (Exception ex) { if (error == null || !error.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await f.Session.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Registry.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Services.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var gate in f.Gates) gate.Dispose();
            if (f.Cloud.Created.Concat(f.Local.Created).Any(p => !p.Disposed)) errors.Add(new InvalidOperationException("Owned OCR provider remains live."));
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });

    internal sealed class Gate : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Pause() { Entered.TrySetResult(); return _release.Task; }
        internal void Open() => _release.TrySetResult();
        public void Dispose() { }
    }
    internal sealed class PausableUi : IUiDispatcher
    {
        private readonly WpfUiDispatcher _actual = new();
        private Gate? _next;
        internal void PauseNext(Gate gate) => Interlocked.Exchange(ref _next, gate);
        public bool CheckAccess() => _actual.CheckAccess();
        public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            var gate = Interlocked.Exchange(ref _next, null);
            if (gate != null) await gate.Pause().ConfigureAwait(false);
            await _actual.InvokeAsync(callback, cancellationToken).ConfigureAwait(false);
        }
    }
    internal sealed class Store : ISettingsService
    {
        public bool IsReadOnly => false;
        internal AppSettings Value = new();
        internal int Updates;
        internal Exception? Failure;
        internal Func<Task>? BeforeUpdate;
        public AppSettings Current => Value;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            if (BeforeUpdate != null) await BeforeUpdate();
            cancellationToken.ThrowIfCancellationRequested(); if (Failure != null) throw Failure;
            var old = Value; Value = mutate(old); Updates++; Changed?.Invoke(this, new(old, Value));
        }
        internal void External(Func<AppSettings, AppSettings> mutate) { var old = Value; Value = mutate(old); Changed?.Invoke(this, new(old, Value)); }
    }
    internal sealed class Secrets : ISecretStore
    {
        internal readonly ConcurrentDictionary<string, string> Values = new();
        internal int Reads, Writes, Removes;
        public event EventHandler<string>? Changed;
        public string? Get(string key) { Interlocked.Increment(ref Reads); return Values.GetValueOrDefault(key); }
        public void Set(string key, string value) { Interlocked.Increment(ref Writes); Values[key] = value; Changed?.Invoke(this, key); }
        public bool Remove(string key) { Interlocked.Increment(ref Removes); var result = Values.TryRemove(key, out _); if (result) Changed?.Invoke(this, key); return result; }
    }
    internal sealed class Observer : IProviderConfigurationObserver
    {
        internal bool Fail;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Fail ? Task.FromException(new IOException("synthetic observer")) : Task.CompletedTask;
    }
    internal sealed class OcrFactory(OcrProviderKind kind) : IOcrProviderFactory
    {
        public OcrProviderKind Kind => kind;
        internal readonly ConcurrentQueue<OcrProvider> Created = new();
        internal Action<OcrProvider>? Configure { get; set; }
        public IOcrProvider Create(OcrProviderProfile profile, ProviderCredentials credentials)
        { var provider = new OcrProvider(profile, credentials); Configure?.Invoke(provider); Created.Enqueue(provider); return provider; }
    }
    internal sealed class OcrProvider(OcrProviderProfile profile, ProviderCredentials credentials) : IOcrProvider, IAsyncDisposable
    {
        internal OcrProviderProfile Profile { get; } = profile;
        internal ProviderCredentials Credentials { get; } = credentials;
        internal int Tests, Recognitions, Disposals;
        internal bool FailDispose, Disposed;
        internal Func<CancellationToken, Task<ProviderTestResult>>? Test { get; set; }
        internal Func<Task>? BeforeDispose { get; set; }
        public ProviderDescriptor Descriptor => new(Profile.Id, Profile.DisplayName, Profile.Kind.ToString(), null, true);
        public ImageConstraints Constraints { get; } = new(1000, false, 100, 40, [ImageFormat.Png]);
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) { Recognitions++; throw new InvalidOperationException("Must not recognize."); }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) { Tests++; return Test?.Invoke(cancellationToken) ?? Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero)); }
        public async ValueTask DisposeAsync() { Disposals++; if (BeforeDispose != null) await BeforeDispose(); if (FailDispose) throw new IOException("synthetic private disposal"); Disposed = true; }
    }
    internal sealed class TranslationFactory : ITranslationProviderFactory
    {
        public TranslationProviderKind Kind => TranslationProviderKind.OpenAICompatible;
        internal Func<CancellationToken, Task<ProviderTestResult>>? Test { get; set; }
        internal int Tests;
        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials) => new TranslationProvider(profile, this);
    }
    private sealed class TranslationProvider(TranslationProviderProfile profile, TranslationFactory owner) : ITranslationProvider
    {
        public ProviderDescriptor Descriptor => new(profile.Id, profile.DisplayName, "fake", profile.Model, true);
        public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(ProviderTranslateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
        public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) { owner.Tests++; return owner.Test?.Invoke(cancellationToken) ?? Task.FromResult(new ProviderTestResult(true, TimeSpan.Zero)); }
    }
}
