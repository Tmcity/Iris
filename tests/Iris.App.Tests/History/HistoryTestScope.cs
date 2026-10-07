using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using Iris.App.History;
using Iris.App.Hosting;
using Iris.App.Settings;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Common;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Serilog.Core;

namespace Iris.App.Tests.History;

internal sealed class HistoryTestScope
{
    internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "iris-t035-" + Guid.NewGuid().ToString("N"));
    internal readonly OcrSettingsUiScope.Store Settings = new();
    internal readonly OcrSettingsUiScope.Secrets Secrets = new();
    internal readonly IClipboardService Clipboard = Substitute.For<IClipboardService>();
    internal readonly IDesktopContext Desktop = Substitute.For<IDesktopContext>();
    internal readonly FakePlacement Placement = new();
    internal readonly TestUi Ui = new();
    internal readonly FakeTimeProvider Time = new();
    internal readonly Factory ProviderFactory = new();
    internal readonly ConcurrentQueue<string> ClipboardWrites = new();
    internal Func<string, CancellationToken, Task>? BeforeClipboard;
    internal Action<HistoryWindow>? ConfigureWindow;
    internal readonly List<Gate> Gates = [];
    internal readonly List<Task> Tasks = [];
    internal readonly List<Action> Undo = [];
    internal IHost Host { get; }
    internal RepositoryBoundary Repository { get; }
    internal HistoryWindowPresenter Presenter { get; }
    internal HistoryWindowSession Session => Presenter.ActiveSession!;
    internal HistoryViewModel Vm => Session.ViewModel!;
    internal HistoryWindow Window => Session.Window!;

    internal HistoryTestScope()
    {
        Directory.CreateDirectory(DirectoryPath);
        Settings.Value = new() { Translation = new() { Providers = [new() { Id = "current-provider", DisplayName = "Current synthetic service", Model = "current-model", Kind = TranslationProviderKind.OpenAICompatible, Enabled = true } ] }, TranslateWindow = new() { HideOnDeactivate = false } };
        Desktop.GetMonitors().Returns(_ => new[] { new MonitorInfo("synthetic", new(0, 0, 1200, 900), new(0, 0, 1200, 900), 1, true) });
        Desktop.GetCursorPosition().Returns(new PixelPoint(500, 400));
        Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            string text = call.Arg<string>(); var ct = call.Arg<CancellationToken>();
            if (BeforeClipboard != null) await BeforeClipboard(text, ct);
            ClipboardWrites.Enqueue(text);
        });
        Host = AppHostBuilder.Build([], new FakeAppPaths(DirectoryPath), new LoggingLevelSwitch(), 0, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ISettingsService>(Settings));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(Secrets));
            services.Replace(ServiceDescriptor.Singleton(Clipboard)); services.Replace(ServiceDescriptor.Singleton(Desktop));
            services.Replace(ServiceDescriptor.Singleton<INativeWindowPlacementAdapter>(Placement));
            services.Replace(ServiceDescriptor.Singleton<IUiDispatcher>(Ui));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
            services.Replace(ServiceDescriptor.Singleton(Substitute.For<IAutoStartService>()));
            services.Replace(ServiceDescriptor.Singleton(Substitute.For<IHotkeyService>()));
            services.Replace(ServiceDescriptor.Singleton(Substitute.For<IHotkeyCaptureService>()));
            services.RemoveAll<ITranslationProviderFactory>(); services.AddSingleton<ITranslationProviderFactory>(ProviderFactory);
            var descriptor = services.Single(d => d.ServiceType == typeof(ITranslationHistoryRepository));
            services.Remove(descriptor);
            services.AddSingleton(sp => new RepositoryBoundary((ITranslationHistoryRepository)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)));
            services.AddSingleton<ITranslationHistoryRepository>(sp => sp.GetRequiredService<RepositoryBoundary>());
            services.Replace(ServiceDescriptor.Transient(sp =>
            {
                var window = new HistoryWindow(Desktop, Placement)
                { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                ConfigureWindow?.Invoke(window);
                return window;
            }));
            services.Replace(ServiceDescriptor.Transient(_ => new TranslateWindow
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 }));
        });
        Repository = Host.Services.GetRequiredService<RepositoryBoundary>();
        Presenter = Host.Services.GetRequiredService<HistoryWindowPresenter>();
    }
    internal static HistoryEntry Entry(string source, bool favorite = false) => new()
    {
        CreatedAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), Origin = TextOrigin.Screenshot,
        SourceText = source, SourceLanguage = "en", TargetLanguage = "zh-CN", IsFavorite = favorite,
        Results = [new("old-a", "Original service A", "old-model-a", "译文中文 % _ OR \"quoted\""),
            new("old-b", "Original service B", "old-model-b", "Second result\n多行译文") { Truncated = true }]
    };
    internal async Task ShowAsync() { await Presenter.ShowAsync(); await Vm.QueryTask; }
    internal async Task SelectAsync(int index = 0) { Vm.SelectedRow = Vm.Rows[index]; await Vm.DetailTask; }
    internal Gate NewGate() { var g = new Gate(); Gates.Add(g); return g; }
    internal T Track<T>(T task) where T : Task { Tasks.Add(task); return task; }
    internal static Task Run(Func<HistoryTestScope, Task> body) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new HistoryTestScope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var gate in f.Gates) gate.Open();
            foreach (var undo in f.Undo) try { undo(); } catch (Exception ex) { errors.Add(ex); }
            f.BeforeClipboard = null; f.Repository.QueryOverride = null; f.Repository.GetOverride = null; f.Repository.BeforeWrite = null;
            f.Placement.Fail = false;
            foreach (var task in f.Tasks)
                try { await task; } catch (OperationCanceledException) { } catch (Exception ex) { if (!ReferenceEquals(primary, ex)) errors.Add(ex); }
            try { await f.Presenter.ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await f.Host.Services.GetRequiredService<TranslateWindowPresenter>().ShutdownAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { f.Host.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            try { Directory.Delete(f.DirectoryPath, true); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
    internal sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Pause() { Entered.TrySetResult(); return _release.Task; }
        internal void Open() => _release.TrySetResult();
    }
    internal sealed class TestUi : IUiDispatcher
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
    internal sealed class FakePlacement : INativeWindowPlacementAdapter
    {
        internal bool Fail;
        public bool SetWindowBounds(nint hWnd, PixelRect bounds, bool show, bool activate) => !Fail;
        public bool TryGetWindowBounds(nint hWnd, out PixelRect bounds) { bounds = new(0, 0, 800, 640); return true; }
    }
    internal sealed class RepositoryBoundary(ITranslationHistoryRepository inner) : ITranslationHistoryRepository, IDisposable
    {
        internal ITranslationHistoryRepository Inner => inner;
        internal readonly ConcurrentQueue<HistoryQuery> Queries = new();
        internal readonly ConcurrentQueue<long> Gets = new();
        internal readonly ConcurrentQueue<(string Kind, long Id, bool Value)> Writes = new();
        internal Func<HistoryQuery, CancellationToken, Task<PagedResult<HistorySummary>>>? QueryOverride;
        internal Func<long, CancellationToken, Task<HistoryEntry?>>? GetOverride;
        internal Func<Task>? BeforeWrite;
        public Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default) => inner.AddAsync(entry, cancellationToken);
        public Task<PagedResult<HistorySummary>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
        { Queries.Enqueue(query); return QueryOverride?.Invoke(query, cancellationToken) ?? inner.QueryAsync(query, cancellationToken); }
        public Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default)
        { Gets.Enqueue(id); return GetOverride?.Invoke(id, cancellationToken) ?? inner.GetAsync(id, cancellationToken); }
        public async Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken cancellationToken = default)
        { Writes.Enqueue(("favorite", id, isFavorite)); if (BeforeWrite != null) await BeforeWrite(); await inner.SetFavoriteAsync(id, isFavorite, cancellationToken); }
        public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        { Writes.Enqueue(("delete", id, false)); if (BeforeWrite != null) await BeforeWrite(); await inner.DeleteAsync(id, cancellationToken); }
        public async Task ClearAsync(bool keepFavorites, CancellationToken cancellationToken = default)
        { Writes.Enqueue(("clear", 0, keepFavorites)); if (BeforeWrite != null) await BeforeWrite(); await inner.ClearAsync(keepFavorites, cancellationToken); }
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }
    internal sealed class Factory : ITranslationProviderFactory
    {
        internal int Created;
        internal readonly ConcurrentQueue<(string Model, ProviderTranslateRequest Request)> Requests = new();
        internal readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TranslationProviderKind Kind => TranslationProviderKind.OpenAICompatible;
        public ITranslationProvider Create(TranslationProviderProfile profile, ProviderCredentials credentials)
        { Interlocked.Increment(ref Created); return new Provider(profile, this); }
        private sealed class Provider(TranslationProviderProfile profile, Factory owner) : ITranslationProvider
        {
            public ProviderDescriptor Descriptor => new(profile.Id, profile.DisplayName, "synthetic", profile.Model, true);
            public async IAsyncEnumerable<TranslationChunk> TranslateStreamAsync(ProviderTranslateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                owner.Requests.Enqueue((profile.Model ?? "", request)); owner.Called.TrySetResult();
                await Task.CompletedTask;
                yield return new("synthetic current translation", FinishReason: TranslationFinishReason.Completed);
            }
            public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("History browsing must not test providers.");
        }
    }
}
