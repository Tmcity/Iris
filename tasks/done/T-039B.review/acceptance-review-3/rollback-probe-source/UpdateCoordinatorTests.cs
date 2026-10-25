using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Iris.App.Tests.TestSupport;
using Iris.App.Updates;
using Iris.Core.Settings;
using Iris.Platform.Windows.Maintenance;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Iris.App.Tests.Updates;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task SdkInitializationFailureStillDisposesItsAlreadyOwnedResources()
    {
        var source = new Source();
        var operation = new UpdateOperation(() => throw new InvalidOperationException("synthetic SDK creation"), source, "unused");
        Should.Throw<InvalidOperationException>(() => operation.Sdk);
        await operation.DisposeAsync(); source.Disposed.ShouldBeTrue(); source.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task AutomaticCheckCancellationAndNotificationKeepOneStartupAttempt()
    {
        using var directory = new TempDirectory(); var source = new Source { Hold = true };
        var current = new AppSettings(); var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(_ => current);
        var notifications = Substitute.For<Iris.Core.Presentation.INotificationService>();
        var coordinator = new AppUpdateCoordinator(new Runtime(directory.Path, source), settings, new FakeTimeProvider(), notifications: notifications);
        coordinator.Start();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var before = current; current = current with { General = current.General with { CheckForUpdates = false } };
            settings.Changed += Raise.EventWith(new SettingsChangedEventArgs(before, current));
            source.Release.TrySetResult(); await coordinator.CancelAsync();
            source.Requests.ShouldBe(1); notifications.DidNotReceiveWithAnyArgs().Notify(default, "", "");
            before = current; current = current with { General = current.General with { CheckForUpdates = true } };
            settings.Changed += Raise.EventWith(new SettingsChangedEventArgs(before, current));
            coordinator.Start(); source.Requests.ShouldBe(1);
            await coordinator.CheckAsync(this); source.Requests.ShouldBe(2); coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Available);
        }
        finally { source.Release.TrySetResult(); await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task ActualSdkLockContentionWithAnotherProcessDoesNotDownloadOrAbandonLockAttempt()
    {
        using var directory = new TempDirectory(); var source = new Source { Package = PackageBytes() };
        string name = "Iris.Update.LockTest." + Guid.NewGuid().ToString("N");
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".entered");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".release");
        string lockPath = Path.Combine(directory.Path, ".velopack_lock").Replace("'", "''", StringComparison.Ordinal);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "$file=[IO.File]::Open('" + lockPath + "',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$entered=[Threading.EventWaitHandle]::OpenExisting('" + name + ".entered');$release=[Threading.EventWaitHandle]::OpenExisting('" + name + ".release');$null=$entered.Set();$null=$release.WaitOne();$file.Dispose();$entered.Dispose();$release.Dispose()" }) start.ArgumentList.Add(argument);
        using var worker = Process.Start(start)!; _ = worker.Handle;
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(new Runtime(directory.Path, source), settings, new FakeTimeProvider());
        coordinator.Start(); Task? download = null;
        try
        {
            (await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken)).ShouldBeTrue();
            await coordinator.CheckAsync(this); var offer = coordinator.Snapshot.OfferId!.Value;
            download = coordinator.DownloadAsync(this, offer);
            await download.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            worker.HasExited.ShouldBeFalse(); source.Downloads.ShouldBe(0); coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Failed);
            await coordinator.StopAsync(); source.Disposed.ShouldBeTrue();
        }
        finally { release.Set(); await worker.WaitForExitAsync(CancellationToken.None); if (download != null) await download; await coordinator.StopAsync(); }
    }
    [Fact]
    public async Task CancellationDuringResourceDisposeStillRequestsAndJoinsItsRealCallback()
    {
        using var directory = new TempDirectory(); var source = new Source { HoldDispose = true };
        using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime(directory.Path, source) { RegisterCallback = token => token.Register(() =>
        { callbackEntered.TrySetResult(); callbackRelease.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue(); }) };
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(runtime, settings, new FakeTimeProvider()); coordinator.Start();
        var work = coordinator.CheckAsync(this); Task? cancel = null;
        try
        {
            await source.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancel = coordinator.CancelAsync(); runtime.OperationToken.IsCancellationRequested.ShouldBeTrue();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            source.DisposeRelease.TrySetResult();
            await source.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            work.IsCompleted.ShouldBeFalse(); cancel.IsCompleted.ShouldBeFalse();
            callbackRelease.Set(); await work; await cancel;
            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Idle);
        }
        finally
        {
            source.DisposeRelease.TrySetResult(); callbackRelease.Set(); await work;
            if (cancel != null) await cancel; await coordinator.StopAsync(); await runtime.CallbackRegistration.DisposeAsync();
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PreparingCandidateIsSingleOwnedWorkAndCloseWaitsForItsRealValidation(bool stop)
    {
        using var directory = new TempDirectory(); var source = new Source { Package = PackageBytes() };
        var runtime = new Runtime(directory.Path, source);
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(runtime, settings, new FakeTimeProvider()); coordinator.Start();
        await coordinator.CheckAsync(this); var offer = coordinator.Snapshot.OfferId!.Value;
        await coordinator.DownloadAsync(this, offer); coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Ready);
        runtime.HoldValidation = true;
        var preparing = coordinator.PrepareApplyAsync(this, offer, TestContext.Current.CancellationToken);
        Task? closing = null;
        try
        {
            await runtime.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            (await coordinator.PrepareApplyAsync(this, offer, TestContext.Current.CancellationToken)).ShouldBeNull();
            closing = stop ? coordinator.StopAsync() : coordinator.CloseOwnerAsync(this);
            runtime.ValidationToken.IsCancellationRequested.ShouldBeTrue(); closing.IsCompleted.ShouldBeFalse();
            runtime.ValidationRelease.TrySetResult();
            (await preparing).ShouldBeNull(); await closing;
            coordinator.Snapshot.Phase.ShouldBe(stop ? UpdatePhase.Stopped : UpdatePhase.Idle);
        }
        finally { runtime.ValidationRelease.TrySetResult(); await preparing; if (closing != null) await closing; await coordinator.StopAsync(); }
    }
    internal static byte[] PackageBytes()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("Iris.nuspec");
            using (var content = new StreamWriter(entry.Open(), Encoding.UTF8))
                content.Write("<package><metadata><id>Iris</id><version>1.1.0</version><channel>win</channel><rid>win-x64</rid><mainExe>Iris.App.exe</mainExe></metadata></package>");
            foreach (string name in new[] { "Iris.App.exe", "Iris.App.runtimeconfig.json", "uia/Iris.UiaHost.exe", "en-US/Iris.App.resources.dll", "maintenance/Iris.Maintenance.exe" })
                using (var content = zip.CreateEntry("lib/app/" + name).Open()) content.WriteByte(1);
        }
        return output.ToArray();
    }
    [Fact]
    public async Task ThrowingAndReentrantSubscriberCannotLeaveUnstartedWorkOrStartIoAfterCancel()
    {
        using var directory = new TempDirectory(); var source = new Source();
        var runtime = new Runtime(directory.Path, source);
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(runtime, settings, new FakeTimeProvider()); coordinator.Start();
        Task? stop = null;
        coordinator.Changed += (_, _) => throw new InvalidOperationException("synthetic subscriber");
        coordinator.Changed += (_, _) => { if (coordinator.Snapshot.Phase == UpdatePhase.Checking) stop = coordinator.CancelAsync(); };
        try
        {
            await coordinator.CheckAsync(this).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            _ = stop.ShouldNotBeNull(); await stop;
            source.Requests.ShouldBe(0); coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Idle);
        }
        finally { await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task LastNotificationBelongsToWorkAndPreventsAdmissionUntilOldStopCompletes()
    {
        using var directory = new TempDirectory(); var source = new Source();
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(new Runtime(directory.Path, source), settings, new FakeTimeProvider()); coordinator.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        coordinator.Changed += (_, _) =>
        {
            if (coordinator.Snapshot.Phase == UpdatePhase.Available) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue(); }
        };
        var work = coordinator.CheckAsync(this); Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            work.IsCompleted.ShouldBeFalse(); stopping = coordinator.CancelAsync(); stopping.IsCompleted.ShouldBeFalse();
            await coordinator.CheckAsync(new object()); source.Requests.ShouldBe(1);
            release.Set(); await work; await stopping;
            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Idle);
        }
        finally { release.Set(); await work; if (stopping != null) await stopping; await coordinator.StopAsync(); }
    }

    [Fact]
    public async Task CancelJoinsActualSdkFeedAndPreservesFailedSourceForCleanupRetry()
    {
        using var directory = new TempDirectory();
        var source = new Source { Hold = true, FailDispose = true };
        var runtime = new Runtime(directory.Path, source);
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(runtime, settings, new FakeTimeProvider());
        coordinator.Start();
        var work = coordinator.CheckAsync(this);
        Task? cancel = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancel = coordinator.CancelAsync(); cancel.IsCompleted.ShouldBeFalse(); source.Disposed.ShouldBeFalse();
            source.Release.TrySetResult(); await work; await cancel;
            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.CleanupPending);
            source.Disposed.ShouldBeFalse(); source.Requests.ShouldBe(1);
            source.FailDispose = false;
            await coordinator.CancelAsync();
            source.Disposed.ShouldBeTrue(); coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.Idle); source.Requests.ShouldBe(1);
        }
        finally
        {
            source.Release.TrySetResult(); source.FailDispose = false;
            await work; if (cancel != null) await cancel; await coordinator.StopAsync();
        }
    }

    [Fact]
    public async Task EmptyFeedDoesNotMeanLatestAndClosingAnUnrelatedWindowDoesNotCancelWork()
    {
        using var directory = new TempDirectory(); var source = new Source { Hold = true, Empty = true };
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings { General = new() { CheckForUpdates = false } });
        var coordinator = new AppUpdateCoordinator(new Runtime(directory.Path, source), settings, new FakeTimeProvider());
        coordinator.Start(); object owner = new(); var work = coordinator.CheckAsync(owner);
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); await coordinator.CloseOwnerAsync(new object());
            work.IsCompleted.ShouldBeFalse(); source.Release.TrySetResult(); await work;
            coordinator.Snapshot.Phase.ShouldBe(UpdatePhase.NoRelease);
        }
        finally { source.Release.TrySetResult(); await work; await coordinator.StopAsync(); }
    }

    internal sealed class Runtime(string directory, Source source) : UpdateRuntime(Substitute.For<IHttpClientFactory>())
    {
        internal bool HoldValidation;
        internal Func<CancellationToken, CancellationTokenRegistration>? RegisterCallback;
        internal CancellationTokenRegistration CallbackRegistration;
        internal CancellationToken OperationToken;
        internal CancellationToken ValidationToken;
        internal readonly TaskCompletionSource ValidationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ValidationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override string Version => "1.0.0";
        internal override ReleaseIdentity Identity() => new("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, "1.0.0");
        internal override UpdateOperation Create(CancellationToken token)
        {
            OperationToken = token;
            if (RegisterCallback != null) CallbackRegistration = RegisterCallback(token);
            var locator = new TestVelopackLocator("Iris", "1.0.0", directory, new NullVelopackLogger());
            return new(new UpdateManager(new ValidatedUpdateSource(source, token), new UpdateOptions { ExplicitChannel = "win", MaximumDeltasBeforeFallback = -1 }, locator), source, directory);
        }
        internal override async Task ValidateAsync(UpdateCandidate candidate, CancellationToken token)
        {
            if (HoldValidation) { ValidationToken = token; ValidationEntered.TrySetResult(); await ValidationRelease.Task; }
            await base.ValidateAsync(candidate, token);
        }
    }
    internal sealed class Source : IUpdateSource, IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Hold, Empty, Disposed, FailDispose, HoldDispose; internal int Requests, Downloads;
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), DisposeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? Package;
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            stagingId.ShouldBeNull(); latestLocalRelease.ShouldBeNull(); Requests++; Entered.TrySetResult(); if (Hold) await Release.Task;
            return new() { Assets = Empty ? [] : [new() { PackageId = "Iris", Version = SemanticVersion.Parse("1.1.0"), Type = VelopackAssetType.Full, FileName = "Iris-1.1.0-full.nupkg", Size = Package?.Length ?? 100, SHA256 = Package != null ? Convert.ToHexString(SHA256.HashData(Package)) : new string('A', 64) }] };
        }
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        { Downloads++; return Package == null ? throw new InvalidOperationException("Checking must not download.") : File.WriteAllBytesAsync(localFile, Package, cancelToken); }
        public async ValueTask DisposeAsync()
        {
            if (FailDispose) throw new System.IO.IOException("owned cleanup failure");
            DisposeEntered.TrySetResult(); if (HoldDispose) await DisposeRelease.Task;
            Disposed = true; DisposeFinished.TrySetResult();
        }
    }
}
