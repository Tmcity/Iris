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

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--lock-worker", var path, var release, var ready]) return CrossProcessSdkLock.Worker(path, release, ready);
        if (args is ["--cross-process-lock"]) return await CrossProcessSdkLock.RunAsync();
        string root = Path.Combine(Path.GetTempPath(), "iris-update-final-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var callbackRelease = new ManualResetEventSlim(false);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledSource();
        var runtime = new ControlledRuntime(root, source, callbackRelease, callbackEntered, callbackEnded);
        var coordinator = new AppUpdateCoordinator(runtime, new Settings(), TimeProvider.System);
        var result = new Result();
        Task? work = null, cancel = null;
        try
        {
            coordinator.Start();
            work = coordinator.CheckAsync(new object());
            await source.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            result.CleanupEnteredBeforeCancel = true;
            cancel = coordinator.CancelAsync();
            result.TokenCancelledBeforeCancelReturned = runtime.OperationToken.IsCancellationRequested;
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            result.CallbackEntered = true;
            source.DisposeRelease.TrySetResult();
            // With both real resource gates reached, give any illegally detached completion a
            // bounded observation interval. The callback remains held throughout this interval.
            try { await Task.WhenAll(work, cancel).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { }
            result.WorkCompletedBeforeCallbackRelease = work.IsCompleted;
            result.CancelCompletedBeforeCallbackRelease = cancel.IsCompleted;
            result.CallbackEndedBeforeRelease = callbackEnded.Task.IsCompleted;
        }
        catch (Exception error) { result.ProbeError = error.GetType().Name; }
        finally
        {
            source.DisposeRelease.TrySetResult();
            callbackRelease.Set();
            try
            {
                if (work != null) await work;
                if (cancel != null) await cancel;
                await coordinator.StopAsync();
                if (result.CallbackEntered) await callbackEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await runtime.CallbackRegistration.DisposeAsync();
                result.AllObservedWorkJoined = true;
            }
            catch (Exception error) { result.ProbeError = error.GetType().Name; }
            // Only this exact generated empty SDK locator directory is removed; no user paths
            // or recursive deletion are involved in the fixture.
            if (Directory.GetFileSystemEntries(root).Length == 0) Directory.Delete(root);
        }
        result.Passed = result.ProbeError == null && result.CleanupEnteredBeforeCancel
            && result.TokenCancelledBeforeCancelReturned && result.CallbackEntered
            && !result.CallbackEndedBeforeRelease && !result.WorkCompletedBeforeCallbackRelease
            && !result.CancelCompletedBeforeCallbackRelease && result.AllObservedWorkJoined;
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.Passed ? 0 : 1;
    }

    private sealed class Settings : ISettingsService
    {
        public bool IsReadOnly => false;
        public AppSettings Current { get; } = new() { General = new() { CheckForUpdates = false } };
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task LoadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No settings I/O is allowed in this fixture.");
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No settings I/O is allowed in this fixture.");
    }
    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network is allowed in this fixture.");
    }
    private sealed class ControlledRuntime(string directory, ControlledSource source,
        ManualResetEventSlim release, TaskCompletionSource entered, TaskCompletionSource ended) : UpdateRuntime(new NoClients())
    {
        internal CancellationToken OperationToken;
        internal CancellationTokenRegistration CallbackRegistration;
        internal override string Version => "1.0.0";
        internal override ReleaseIdentity Identity() => new("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, "1.0.0");
        internal override UpdateOperation Create(CancellationToken token)
        {
            OperationToken = token;
            CallbackRegistration = token.Register(() =>
            {
                entered.TrySetResult();
                try { if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Review callback safety deadline reached."); }
                finally { ended.TrySetResult(); }
            });
            var locator = new TestVelopackLocator("Iris", "1.0.0", directory, new NullVelopackLogger());
            return new(new UpdateManager(new ValidatedUpdateSource(source, token),
                new UpdateOptions { ExplicitChannel = "win", MaximumDeltasBeforeFallback = -1 }, locator), source, directory);
        }
    }
    private sealed class ControlledSource : IUpdateSource, IAsyncDisposable
    {
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => Task.FromResult(new VelopackAssetFeed { Assets = [new() { PackageId = "Iris", Version = SemanticVersion.Parse("1.1.0"),
                Type = VelopackAssetType.Full, FileName = "Iris-1.1.0-full.nupkg", Size = 100, SHA256 = new string('A', 64) }] });
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default) => throw new InvalidOperationException("A check must not download.");
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await DisposeRelease.Task.ConfigureAwait(false);
        }
    }
    private sealed class Result
    {
        public bool CleanupEnteredBeforeCancel { get; set; }
        public bool TokenCancelledBeforeCancelReturned { get; set; }
        public bool CallbackEntered { get; set; }
        public bool WorkCompletedBeforeCallbackRelease { get; set; }
        public bool CancelCompletedBeforeCallbackRelease { get; set; }
        public bool CallbackEndedBeforeRelease { get; set; }
        public bool AllObservedWorkJoined { get; set; }
        public string? ProbeError { get; set; }
        public bool Passed { get; set; }
    }
}
