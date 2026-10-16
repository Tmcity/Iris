using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.DependencyInjection;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Iris.Providers.Tests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Iris.App.Tests.Settings;

internal sealed class NetworkSettingsUiScope : IAsyncDisposable
{
    internal readonly TempDirectory Directory = new();
    internal readonly OcrSettingsUiScope.Secrets Secrets = new();
    internal readonly OcrSettingsUiScope.TranslationFactory Translation = new();
    internal readonly OcrSettingsUiScope.OcrFactory Cloud = new(OcrProviderKind.BaiduCloud), Local = new(OcrProviderKind.WindowsLocal);
    internal readonly FakeTimeProvider Time = new();
    internal readonly NetworkWire Wire = new() { RequireAuth = false };
    internal readonly UiDispatcher Ui = new();
    internal readonly Observer NetworkObserver = new();
    internal readonly List<Gate> Gates = [];
    internal readonly List<Action> Restore = [];
    internal readonly List<(Task Work, Type? Failure)> Work = [];
    internal readonly TransportFactory Transport;
    internal ServiceProvider Services = null!;
    internal IServiceScope Scope = null!;
    internal ISettingsService Settings = null!;
    internal SettingsViewModel Vm = null!;
    internal SettingsWindow Window = null!;
    internal SettingsWindowSession Session = null!;
    internal int Updates;
    internal string SettingsFile => Path.Combine(Directory.Path, "settings.json");
    internal ProxyConnectivityTester Tester => Services.GetRequiredService<ProxyConnectivityTester>();
    internal SettingsApplyReceiptManager Receipts => Services.GetRequiredService<SettingsApplyReceiptManager>();
    internal NetworkSettingsUiScope() { Transport = new(Wire); Wire.Respond = (_, _) => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"; }

    internal static AppSettings Initial() => new()
    {
        General = new() { Theme = AppTheme.Light }, Network = new() { ProxyMode = ProxyMode.None },
        Translation = new() { Providers = [new() { Id = "same", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Saved translation", BaseUrl = "http://translation.example.test/v1", Model = "synthetic", Enabled = false }] },
        Ocr = new() { Providers = [new() { Id = "same", Kind = OcrProviderKind.BaiduCloud, DisplayName = "Saved OCR", BaiduOptions = new() { BaseUrl = "http://ocr.example.test/" } },
            new() { Id = BuiltInProviderIds.WindowsOcr, Kind = OcrProviderKind.WindowsLocal, DisplayName = "Local", WindowsOptions = new() }] }
    };
    private async Task InitializeAsync(AppSettings initial, bool readOnly, Action<IServiceCollection>? configure = null)
    {
        var text = JsonSerializer.Serialize(initial, IrisJson.Options);
        if (readOnly) text = text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2147483648", StringComparison.Ordinal);
        await File.WriteAllTextAsync(SettingsFile, text);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IAppPaths>(new FakeAppPaths(Directory.Path));
        services.AddIrisInfrastructure(); services.AddIrisCore(); services.AddIrisProviders();
        services.AddSingleton<ISecretStore>(Secrets); services.AddSingleton<TimeProvider>(Time);
        services.RemoveAll<ITranslationProviderFactory>(); services.AddSingleton<ITranslationProviderFactory>(Translation);
        services.RemoveAll<IOcrProviderFactory>(); services.AddSingleton<IOcrProviderFactory>(Cloud); services.AddSingleton<IOcrProviderFactory>(Local);
        services.AddSingleton<INetworkConfigurationObserver>(NetworkObserver); services.AddSingleton<ProxyConnectivityTransportFactory>(Transport);
        services.AddSingleton(Substitute.For<IAutoStartService>()); services.AddSingleton(Substitute.For<ITranslationHistoryRepository>());
        services.AddSingleton<IUiDispatcher>(Ui); services.AddSingleton<ThemeService>();
        services.AddSingleton<SettingsApplyReceiptManager>(); services.AddSingleton<ISettingsApplyReceiptManager>(sp => sp.GetRequiredService<SettingsApplyReceiptManager>());
        services.AddScoped<GeneralSettingsSaveCoordinator>(); services.AddScoped<SettingsViewModel>();
        services.AddSingleton<SettingsWindowPresenter>();
        services.AddTransient(_ => new SettingsWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 });
        configure?.Invoke(services);
        Services = services.BuildServiceProvider(); Settings = Services.GetRequiredService<ISettingsService>(); await Settings.LoadAsync();
        Settings.Changed += (_, _) => Updates++;
        Scope = Services.CreateScope(); Vm = Scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        Window = new() { DataContext = Vm, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        Session = new(Scope, Window, Vm, Services.GetRequiredService<ThemeService>(), Scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>());
    }
    internal void Show() { Vm.CurrentPageKey = "network"; Window.Show(); Window.UpdateLayout(); }
    internal void Select(NetworkEndpointKind kind = NetworkEndpointKind.Translation) => Vm.SelectedNetworkEndpoint = Vm.NetworkEndpoints.First(c => c.Selection.Kind == kind);
    internal Gate NewGate() { var gate = new Gate(); Gates.Add(gate); return gate; }
    internal T Track<T>(T task, Type? failure = null) where T : Task { Work.Add((task, failure)); return task; }
    internal async Task IdleAsync() => await Window.Dispatcher.InvokeAsync(Window.UpdateLayout, DispatcherPriority.ApplicationIdle);

    internal static Task Run(Func<NetworkSettingsUiScope, Task> body, AppSettings? initial = null, bool readOnly = false, Action<IServiceCollection>? configure = null) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new NetworkSettingsUiScope(); Exception? primary = null; var cleanup = new List<Exception>();
        try { await f.InitializeAsync(initial ?? Initial(), readOnly, configure); await body(f); }
        catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var action in f.Restore) action(); foreach (var gate in f.Gates) gate.Open(); f.Ui.Clear(); f.Wire.ReleaseAll(); f.NetworkObserver.Fail = false;
            foreach (var provider in f.Cloud.Created.Concat(f.Local.Created)) { provider.FailDispose = false; provider.BeforeDispose = null; }
            foreach (var (work, failure) in f.Work) try { await work; } catch (Exception ex) { if (failure == null || !failure.IsInstanceOfType(ex)) cleanup.Add(ex); }
            try { await f.DisposeAsync(); } catch (Exception ex) { cleanup.Add(ex); }
            foreach (var gate in f.Gates) gate.Dispose();
        }
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    });

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        if (Session != null) try { await Session.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
        if (Services != null)
        {
            try { await Services.GetRequiredService<SettingsWindowPresenter>().ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await Services.GetRequiredService<IOcrProviderRegistry>().StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await Services.GetRequiredService<INetworkTransportLifetime>().StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await Services.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        Directory.Dispose();
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    internal sealed class Gate : IDisposable
    {
        private readonly ManualResetEventSlim _sync = new(); private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task PauseAsync() { Entered.TrySetResult(); return _done.Task; }
        internal void Pause() { Entered.TrySetResult(); _sync.Wait(); }
        internal void Open() { _sync.Set(); _done.TrySetResult(); }
        public void Dispose() => _sync.Dispose();
    }
    internal sealed class UiDispatcher : IUiDispatcher
    {
        private readonly WpfUiDispatcher _inner = new();
        private (Func<bool> Predicate, Gate Gate, bool After)? _pause;
        internal event Action<Task>? InvocationStarted;
        internal void PauseWhen(Func<bool> predicate, Gate gate, bool after = true) => _pause = (predicate, gate, after);
        internal void Clear() => _pause = null;
        public bool CheckAccess() => _inner.CheckAccess();
        public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            var work = InvokeCoreAsync(callback, cancellationToken);
            InvocationStarted?.Invoke(work);
            return work;
        }
        private async Task InvokeCoreAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            Gate? before = null, after = null;
            await _inner.InvokeAsync(() =>
            {
                if (_pause is { After: false } pause && pause.Predicate()) { before = pause.Gate; _pause = null; }
                return Task.CompletedTask;
            }, cancellationToken);
            if (before != null) await before.PauseAsync();
            await _inner.InvokeAsync(async () =>
            {
                await callback();
                if (_pause is { After: true } pause && pause.Predicate()) { after = pause.Gate; _pause = null; }
            }, cancellationToken);
            if (after != null) await after.PauseAsync();
        }
    }
    internal sealed class TransportFactory(NetworkWire wire) : ProxyConnectivityTransportFactory
    {
        internal int Creates, Sends;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Send;
        internal override HttpMessageHandler Create(NetworkRoute route)
        {
            Creates++;
            if (Send != null) return new Handler(this);
            var socket = (SocketsHttpHandler)base.Create(route); socket.ConnectCallback = wire.ConnectAsync; return socket;
        }
        private sealed class Handler(TransportFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { Interlocked.Increment(ref owner.Sends); return owner.Send!(request, cancellationToken); }
        }
    }
    internal sealed class Observer : INetworkConfigurationObserver
    {
        internal bool Fail; internal int Calls;
        public Task ApplyAsync(NetworkSettings settings, Guid generationId, CancellationToken cancellationToken = default)
        { Calls++; return Fail ? Task.FromException(new IOException("synthetic apply")) : Task.CompletedTask; }
    }
    internal sealed class Content : ByteArrayContent
    {
        internal Content() : base([]) { }
        internal bool Fail;
        internal Action? BeforeDispose { get; set; }
        protected override void Dispose(bool disposing) { if (disposing) { BeforeDispose?.Invoke(); if (Fail) throw new IOException("synthetic-content"); } base.Dispose(disposing); }
    }
}
