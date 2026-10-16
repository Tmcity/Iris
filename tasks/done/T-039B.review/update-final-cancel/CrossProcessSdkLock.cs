using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Iris.App.Updates;
using Iris.Core.Settings;
using Iris.Platform.Windows.Maintenance;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

internal static class CrossProcessSdkLock
{
    internal static int Worker(string path, string releaseName, string readyName)
    {
        using var release = EventWaitHandle.OpenExisting(releaseName);
        using var ready = EventWaitHandle.OpenExisting(readyName);
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ready.Set();
        return release.WaitOne(TimeSpan.FromSeconds(30)) ? 0 : 3;
    }

    internal static async Task<int> RunAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "iris-update-process-lock-" + suffix);
        Directory.CreateDirectory(root);
        string lockPath = Path.Combine(root, ".velopack_lock");
        string releaseName = "Local\\Iris.Review.SdkLock.Release." + suffix;
        string readyName = "Local\\Iris.Review.SdkLock.Ready." + suffix;
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var source = new Source();
        var runtime = new Runtime(root, source);
        var coordinator = new AppUpdateCoordinator(runtime, new Settings(), TimeProvider.System);
        Process? child = null;
        Task? work = null, cancel = null;
        var result = new Result();
        try
        {
            coordinator.Start();
            await coordinator.CheckAsync(new object());
            Guid offer = coordinator.Snapshot.OfferId ?? throw new InvalidOperationException("Expected controlled SDK offer.");
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Iris.App.Tests.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in new[] { "--lock-worker", lockPath, releaseName, readyName }) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new IOException("Review worker did not start.");
            _ = child.Handle;
            if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(10)))) throw new TimeoutException("Review worker did not acquire its file.");
            result.SeparateWorkerOwnsFile = true;
            work = coordinator.DownloadAsync(new object(), offer);
            // The override only observes the task returned by base.AcquireUpdateLock; the SDK's
            // actual lock implementation and the worker's actual cross-process file exclusion run.
            await runtime.AcquireAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            result.SdkAcquirePending = runtime.AcquireWasPending;
            cancel = coordinator.CancelAsync();
            result.TokenCancelledOnReturn = runtime.LastToken.IsCancellationRequested;
            try { await Task.WhenAll(work, cancel).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { }
            result.WorkCompletedWhileOtherProcessOwnsLock = work.IsCompleted;
            result.CancelCompletedWhileOtherProcessOwnsLock = cancel.IsCompleted;
            result.DownloadCallsWhileLocked = source.DownloadCalls;
        }
        catch (Exception error) { result.ProbeError = error.GetType().Name; }
        finally
        {
            release.Set();
            try
            {
                if (child != null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    await child.WaitForExitAsync(timeout.Token);
                    result.WorkerExitCode = child.ExitCode;
                }
                if (work != null) await work;
                if (cancel != null) await cancel;
                result.DownloadCallsAfterRelease = source.DownloadCalls;
                result.PhaseAfterCancel = coordinator.Snapshot.Phase.ToString();
                await coordinator.StopAsync();
                result.AllWorkJoined = true;
            }
            catch (Exception error) { result.ProbeError = error.GetType().Name; }
            finally { child?.Dispose(); }
            if (File.Exists(lockPath)) File.Delete(lockPath);
            if (Directory.GetFileSystemEntries(root).Length == 0) Directory.Delete(root);
        }
        result.Passed = result.ProbeError == null && result.SeparateWorkerOwnsFile && result.SdkAcquirePending
            && result.TokenCancelledOnReturn && !result.WorkCompletedWhileOtherProcessOwnsLock
            && !result.CancelCompletedWhileOtherProcessOwnsLock && result.DownloadCallsWhileLocked == 0
            && result.DownloadCallsAfterRelease == 0 && result.WorkerExitCode == 0
            && result.PhaseAfterCancel == nameof(UpdatePhase.Idle) && result.AllWorkJoined;
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.Passed ? 0 : 1;
    }

    private sealed class Settings : ISettingsService
    {
        public bool IsReadOnly => false;
        public AppSettings Current { get; } = new() { General = new() { CheckForUpdates = false } };
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task LoadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No settings I/O is allowed.");
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No settings I/O is allowed.");
    }
    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network is allowed.");
    }
    private sealed class Runtime(string root, Source source) : UpdateRuntime(new NoClients())
    {
        internal readonly TaskCompletionSource AcquireAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool AcquireWasPending;
        internal CancellationToken LastToken;
        internal override string Version => "1.0.0";
        internal override ReleaseIdentity Identity() => new("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, "1.0.0");
        internal override UpdateOperation Create(CancellationToken token)
        {
            LastToken = token;
            var locator = new TestVelopackLocator("Iris", "1.0.0", root, new NullVelopackLogger());
            return new(new ObservedManager(new ValidatedUpdateSource(source, token), locator, this), source, root);
        }
    }
    private sealed class ObservedManager(IUpdateSource source, IVelopackLocator locator, Runtime runtime)
        : UpdateManager(source, new UpdateOptions { ExplicitChannel = "win", MaximumDeltasBeforeFallback = -1 }, locator)
    {
        protected override async Task<IDisposable> AcquireUpdateLock()
        {
            var actual = base.AcquireUpdateLock();
            runtime.AcquireWasPending = !actual.IsCompleted;
            runtime.AcquireAttempted.TrySetResult();
            return await actual.ConfigureAwait(false);
        }
    }
    private sealed class Source : IUpdateSource, IAsyncDisposable
    {
        internal int DownloadCalls;
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => Task.FromResult(new VelopackAssetFeed { Assets = [new() { PackageId = "Iris", Version = SemanticVersion.Parse("1.1.0"),
                Type = VelopackAssetType.Full, FileName = "Iris-1.1.0-full.nupkg", Size = 100, SHA256 = new string('A', 64) }] });
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default)
        { DownloadCalls++; throw new InvalidOperationException("No source download may start after cancellation."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Result
    {
        public bool SeparateWorkerOwnsFile { get; set; }
        public bool SdkAcquirePending { get; set; }
        public bool TokenCancelledOnReturn { get; set; }
        public bool WorkCompletedWhileOtherProcessOwnsLock { get; set; }
        public bool CancelCompletedWhileOtherProcessOwnsLock { get; set; }
        public int DownloadCallsWhileLocked { get; set; }
        public int DownloadCallsAfterRelease { get; set; }
        public int? WorkerExitCode { get; set; }
        public string? PhaseAfterCancel { get; set; }
        public bool AllWorkJoined { get; set; }
        public string? ProbeError { get; set; }
        public bool Passed { get; set; }
    }
}
