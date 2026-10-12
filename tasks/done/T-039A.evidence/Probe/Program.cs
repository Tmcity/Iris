using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args[0] == "boot") return Boot(args.Skip(1).ToArray());
        Run(args[0], args[1], args[2]).GetAwaiter().GetResult();
        return 0;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Log(string name, object value) => Console.WriteLine(name + " " + JsonSerializer.Serialize(value));
    private static async Task Run(string packId, string feedDirectory, string root)
    {
        Directory.CreateDirectory(root);
        Log("SDK", new { Version = VelopackRuntimeInfo.VelopackNugetVersion, Check = typeof(UpdateManager).GetMethod("CheckForUpdatesAsync")!.ToString(), Feed = typeof(IUpdateSource).GetMethod("GetReleaseFeed")!.ToString() });
        var local = new SimpleFileSource(new DirectoryInfo(feedDirectory));
        var sourceFeed = await local.GetReleaseFeed(new NullVelopackLogger(), packId, "win");
        Require(sourceFeed.Assets.Any(x => x.Type == VelopackAssetType.Delta), "actual delta package missing");
        var full = sourceFeed.Assets.Where(x => x.Type == VelopackAssetType.Full).OrderBy(x => x.Version).ToArray();
        Require(full.Length == 2, "two versions required");
        SafeLocator Locator(string name, string version = "1.0.0") => new(packId, version, Path.Combine(root, name));
        UpdateManager Manager(IUpdateSource source, SafeLocator locator) => new(source, new UpdateOptions { ExplicitChannel = "win", MaximumDeltasBeforeFallback = -1 }, locator);

        var successLocator = Locator("success"); var manager = Manager(local, successLocator);
        var update = (await manager.CheckForUpdatesAsync())!;
        Require(update.TargetFullRelease.Version.ToString() == "1.1.0", "incorrect target");
        await manager.DownloadUpdatesAsync(update);
        var package = Path.Combine(successLocator.PackagesDir!, update.TargetFullRelease.FileName);
        Require(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package))) == update.TargetFullRelease.SHA256, "package checksum");
        Require(successLocator.Recorder.Starts.Count == 0, "download launched updater");
        Require(await Manager(local, Locator("up-to-date", "1.1.0")).CheckForUpdatesAsync() == null, "same version should not update");
        Log("LOCAL_TWO_VERSION", new { Target = update.TargetFullRelease.Version.ToString(), Bytes = new FileInfo(package).Length, UpdateExeExtractedButNotRun = File.Exists(successLocator.UpdateExePath), SourceAssets = sourceFeed.Assets.Length });

        var corruptLocator = Locator("corrupt"); var corrupt = new ControlledSource(local) { Corrupt = true };
        var corruptManager = Manager(corrupt, corruptLocator);
        try { await corruptManager.DownloadUpdatesAsync((await corruptManager.CheckForUpdatesAsync())!); throw new Exception("checksum was accepted"); }
        catch (Exception ex) when (ex.GetType().Name == "ChecksumFailedException") { Log("CORRUPTION_REJECTED", new { Exception = ex.GetType().Name, RemainingPackages = Directory.GetFiles(corruptLocator.PackagesDir!, "*.nupkg").Length, RemainingPartials = Directory.GetFiles(corruptLocator.PackagesDir!, "*.partial").Length }); }

        // SDK early-returns for an already existing target file, without re-verifying it.
        var cachedLocator = Locator("cached-corrupt"); var cached = Manager(local, cachedLocator);
        var cachedUpdate = (await cached.CheckForUpdatesAsync())!;
        var cachedPath = Path.Combine(cachedLocator.PackagesDir!, cachedUpdate.TargetFullRelease.FileName);
        await File.WriteAllTextAsync(cachedPath, "owned corrupt cache");
        await cached.DownloadUpdatesAsync(cachedUpdate);
        Require(new FileInfo(cachedPath).Length != cachedUpdate.TargetFullRelease.Size, "raw cache behavior changed");
        Log("RAW_EXISTING_CACHE_NOT_VERIFIED", new { Length = new FileInfo(cachedPath).Length, Expected = cachedUpdate.TargetFullRelease.Size });
        Require(!await IsReady(cachedPath, cachedUpdate.TargetFullRelease), "corrupt cache entered installable state");
        Require(await IsReady(package, update.TargetFullRelease), "valid candidate not recognized");
        Log("OWNED_CACHE_ADMISSION", new { CorruptReady = false, ValidReady = true, UpdaterStarts = cachedLocator.Recorder.Starts.Count });

        // A cancelled download whose source ignores cancellation may still finish verification/move in the raw SDK.
        var rawLocator = Locator("raw-cancel"); var rawSource = new ControlledSource(local) { DownloadGate = new Gate() };
        var rawManager = Manager(rawSource, rawLocator); using var rawCancel = new CancellationTokenSource();
        var rawWork = rawManager.DownloadUpdatesAsync((await rawManager.CheckForUpdatesAsync())!, cancelToken: rawCancel.Token);
        try
        {
            await rawSource.DownloadGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); rawCancel.Cancel();
            Require(!rawWork.IsCompleted && rawSource.OpenStreams == 1, "old I/O was not owned");
            rawSource.DownloadGate.Release.TrySetResult(); await rawWork;
            Require(File.Exists(Path.Combine(rawLocator.PackagesDir!, full[1].FileName)), "raw late completion behavior changed");
            Log("RAW_LATE_DOWNLOAD_AFTER_CANCEL", new { SdkTaskCompletedSuccessfully = rawWork.IsCompletedSuccessfully, PackagePublished = true, StreamsAfterJoin = rawSource.OpenStreams });
        }
        finally { rawSource.DownloadGate.Release.TrySetResult(); await rawWork; await rawSource.DisposeAsync(); }

        // The owned source supplies the token missing from GetReleaseFeed; the operation checks before publishing.
        foreach (bool check in new[] { true, false })
        {
            var locator = Locator(check ? "owned-check" : "owned-download");
            using var cancel = new CancellationTokenSource(); var gate = new Gate();
            var source = new ControlledSource(local) { Token = cancel.Token, EnforceToken = true, FailDisposeOnce = true, FeedGate = check ? gate : null, DownloadGate = check ? null : gate };
            var sdk = Manager(source, locator); var selected = check ? null : await sdk.CheckForUpdatesAsync();
            var operation = new OwnedOperation(source, cancel);
            operation.Work = Task.Run(async () => { if (check) await sdk.CheckForUpdatesAsync(); else await sdk.DownloadUpdatesAsync(selected!, cancelToken: cancel.Token); cancel.Token.ThrowIfCancellationRequested(); operation.Published = true; });
            Task? stop = null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop = operation.StopAsync();
                Require(!stop.IsCompleted && source.OpenStreams == 1 && !source.Disposed, "stop abandoned real I/O");
                gate.Release.TrySetResult();
                try { await stop; throw new Exception("cleanup failure missing"); } catch (IOException) { }
                Require(!operation.Published && !source.Disposed && source.OpenStreams == 0, "failed cleanup lost original source");
                await operation.StopAsync();
                Require(source.Disposed && source.Requests == 1, "cleanup retry restarted operation");
                Log("OWNED_CANCEL_JOIN_RETRY", new { Phase = check ? "check" : "download", Published = operation.Published, Requests = source.Requests, DisposeAttempts = source.DisposeAttempts, Streams = source.OpenStreams });
            }
            finally
            {
                gate.Release.TrySetResult();
                try { if (stop != null) await stop; } catch (IOException) { }
                await operation.StopAsync();
            }
        }

        foreach (string phase in new[] { "lock", "checksum", "cleanup" })
        {
            using var cancellation = new CancellationTokenSource(); var gate = new Gate();
            var phaseSource = new ControlledSource(local) { Token = cancellation.Token, EnforceToken = true };
            var phaseManager = new PhaseManager(phaseSource, Locator("phase-" + phase), gate, phase);
            var phaseUpdate = (await phaseManager.CheckForUpdatesAsync())!;
            var owner = new OwnedOperation(phaseSource, cancellation);
            owner.Work = Task.Run(async () => { await phaseManager.DownloadUpdatesAsync(phaseUpdate, cancelToken: cancellation.Token); cancellation.Token.ThrowIfCancellationRequested(); owner.Published = true; });
            Task? stopping = null;
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stopping = owner.StopAsync();
                Require(!stopping.IsCompleted && !phaseSource.Disposed, "SDK phase abandoned: " + phase);
                gate.Open(); await stopping;
                Require(!owner.Published && phaseSource.Disposed && phaseSource.OpenStreams == 0, "phase ownership lost");
                Log("SDK_PHASE_JOIN", new { Phase = phase, Requests = phaseSource.Requests, Published = owner.Published, Streams = phaseSource.OpenStreams });
            }
            finally { gate.Open(); if (stopping != null) await stopping; await owner.StopAsync(); }
        }

        // Private repository responses are synthetic; no request leaves the process and no credential is supplied.
        var privateDownloader = new PrivateDownloader();
        var privateManager = Manager(new GithubSource("https://github.com/Tmcity/Iris", null, false, privateDownloader), Locator("private"));
        try { await privateManager.CheckForUpdatesAsync(); throw new Exception("private repository reported up-to-date"); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { Log("PRIVATE_SOURCE_UNAVAILABLE", new { Requests = privateDownloader.Requests, NoCredentials = true, NotReportedAsLatest = true }); }

        // The real SDK dispatches through IProcessImpl, which this prototype records and never executes.
        var cleaned = new Gate(); var process = successLocator.Recorder;
        var handoff = Task.Run(async () => { await cleaned.Wait(); manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true); });
        try
        {
            await cleaned.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Require(process.Starts.Count == 0, "handoff before cleanup");
            cleaned.Release.TrySetResult(); await handoff;
            Require(process.Starts.Count == 1 && process.ExitCode == null, "safe API unexpectedly exits");
            Require(process.Starts[0].Contains("--waitPid") && process.Starts[0].Contains("4242"), "handoff does not wait for owner");
            manager.ApplyUpdatesAndExit(update.TargetFullRelease);
            Require(process.ExitCode == 0, "unsafe API did not call process Exit");
            Log("SDK_HANDOFF_RECORDED_ONLY", new { Calls = process.Starts, UnsafeExit = process.ExitCode });
        }
        finally { cleaned.Release.TrySetResult(); await handoff; }
        Console.WriteLine("ALL_PROBES_PASSED");
    }

    private static async Task<bool> IsReady(string path, VelopackAsset asset)
    {
        if (Path.GetFileName(path) != asset.FileName || asset.Type != VelopackAssetType.Full || asset.SHA256?.Length != 64) return false;
        var file = new FileInfo(path);
        return file.Exists && file.Length == asset.Size && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) == asset.SHA256;
    }

    private static int Boot(string[] args)
    {
        var directory = args[0]; var mode = args[1]; var locator = new SafeLocator("Iris.T039A.Boot", "1.0.0", directory);
        locator.Recorder.RealExit = mode == "real-exit";
        if (mode is "auto-on" or "auto-off")
        {
            foreach (var file in Directory.GetFiles(args[2], "*.nupkg")) File.Copy(file, Path.Combine(locator.PackagesDir!, Path.GetFileName(file)));
            File.WriteAllText(locator.UpdateExePath!, "owned updater path; never executed");
        }
        var app = VelopackApp.Build().SetLocator(locator).SetArgs(mode is "auto-on" or "auto-off" ? [] : args.Skip(2).ToArray());
        if (mode != "auto-on") app.SetAutoApplyOnStartup(false);
        void Hook(string name) { Log("HOOK", new { Name = name, Thread = Environment.CurrentManagedThreadId, Apartment = Thread.CurrentThread.GetApartmentState().ToString() }); if (mode == "throw") throw new IOException("synthetic-hook"); }
        app.OnAfterInstallFastCallback(_ => Hook("install")).OnBeforeUninstallFastCallback(_ => Hook("uninstall"))
            .OnBeforeUpdateFastCallback(_ => Hook("obsolete")).OnAfterUpdateFastCallback(_ => Hook("updated"))
            .OnFirstRun(_ => Hook("first-run")).OnRestarted(_ => Hook("restarted"));
        try { app.Run(); Log("BOOT_RETURN", new { locator.Recorder.ExitCode, NormalMainEligible = locator.Recorder.ExitCode == null, Starts = locator.Recorder.Starts.Count, PackagesRemaining = Directory.GetFiles(locator.PackagesDir!, "*.nupkg").Select(Path.GetFileName).ToArray() }); }
        finally { Console.WriteLine("BOOT_FINALLY"); }
        return 0;
    }
}

internal sealed class Gate
{
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new(); private bool _released;
    internal Task Wait() { Entered.TrySetResult(); return Release.Task; }
    internal void WaitSync() { Entered.TrySetResult(); lock (_sync) { if (!_released && !Monitor.Wait(_sync, TimeSpan.FromSeconds(10))) throw new TimeoutException("owned synchronous SDK phase gate"); } }
    internal void Open() { lock (_sync) { _released = true; Monitor.PulseAll(_sync); } Release.TrySetResult(); }
}
internal sealed class Recorder : IProcessImpl
{
    internal readonly List<string[]> Starts = []; internal int? ExitCode; internal bool RealExit;
    public string GetCurrentProcessPath() => Environment.ProcessPath!;
    public uint GetCurrentProcessId() => 4242;
    public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) => Starts.Add(args.ToArray());
    public void Exit(int exitCode) { ExitCode = exitCode; Console.WriteLine("PROCESS_EXIT " + exitCode); if (RealExit) Environment.Exit(exitCode); }
}
internal sealed class SafeLocator : TestVelopackLocator
{
    internal readonly Recorder Recorder = new();
    internal SafeLocator(string id, string version, string root) : base(id, version, Make(root, "packages"), Make(root, "current"), root, Path.Combine(root, "Update.exe"), "win") { }
    private static string Make(string root, string child) => Directory.CreateDirectory(Path.Combine(root, child)).FullName;
    public override IProcessImpl Process => Recorder;
    public override string? AppUserModelId => null;
}
internal sealed class ControlledSource(SimpleFileSource inner) : IUpdateSource, IAsyncDisposable
{
    internal Gate? FeedGate, DownloadGate; internal CancellationToken Token; internal bool EnforceToken, Corrupt, FailDisposeOnce, Disposed;
    internal int OpenStreams, Requests, DisposeAttempts;
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (FeedGate != null)
        {
            Requests++; OpenStreams++;
            try { using var input = File.OpenRead(Path.Combine(inner.BaseDirectory.FullName, "releases.win.json")); await FeedGate.Wait(); if (EnforceToken) Token.ThrowIfCancellationRequested(); }
            finally { OpenStreams--; }
        }
        return await inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        if (EnforceToken) Token.ThrowIfCancellationRequested();
        Requests++; OpenStreams++;
        try
        {
            using var input = File.OpenRead(Path.Combine(inner.BaseDirectory.FullName, releaseEntry.FileName));
            if (DownloadGate != null) await DownloadGate.Wait();
            if (EnforceToken) Token.ThrowIfCancellationRequested();
            using var output = File.Create(localFile); await input.CopyToAsync(output);
            if (Corrupt) output.WriteByte(0);
        }
        finally { OpenStreams--; }
    }
    public ValueTask DisposeAsync()
    {
        if (Disposed) return ValueTask.CompletedTask;
        DisposeAttempts++;
        if (FailDisposeOnce && DisposeAttempts == 1) throw new IOException("synthetic cleanup");
        if (OpenStreams != 0) throw new InvalidOperationException("disposed while I/O active");
        Disposed = true; return ValueTask.CompletedTask;
    }
}
internal sealed class PhaseManager(IUpdateSource source, IVelopackLocator locator, Gate gate, string phase)
    : UpdateManager(source, new UpdateOptions { ExplicitChannel = "win", MaximumDeltasBeforeFallback = -1 }, locator)
{
    protected override async Task<IDisposable> AcquireUpdateLock()
    {
        var lease = await base.AcquireUpdateLock();
        try { if (phase == "lock") await gate.Wait(); return lease; } catch { lease.Dispose(); throw; }
    }
    protected override async Task VerifyPackageChecksumAsync(VelopackAsset release, string? filePathOverride = null)
    { if (phase == "checksum") await gate.Wait(); await base.VerifyPackageChecksumAsync(release, filePathOverride); }
    protected override void CleanPackagesExcept(string? assetToKeep)
    { if (phase == "cleanup") gate.WaitSync(); base.CleanPackagesExcept(assetToKeep); }
}
internal sealed class PrivateDownloader : IFileDownloader
{
    internal readonly List<string> Requests = [];
    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        if (headers?.ContainsKey("Authorization") == true) throw new Exception("unexpected repository credential");
        Requests.Add(url);
        throw new HttpRequestException("synthetic private repository not found", null, System.Net.HttpStatusCode.NotFound);
    }
    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) => throw new Exception("unexpected asset request");
    public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) => throw new Exception("unexpected package request");
}
internal sealed class OwnedOperation(ControlledSource source, CancellationTokenSource cancel)
{
    internal Task Work = Task.CompletedTask; internal bool Published;
    internal async Task StopAsync()
    {
        cancel.Cancel();
        try { await Work; } catch (OperationCanceledException) { }
        await source.DisposeAsync();
    }
}
