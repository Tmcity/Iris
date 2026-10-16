using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Iris.Platform.Windows.Maintenance;
using Shouldly;

namespace Iris.Maintenance.Tests;

public sealed class MaintenanceTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void HandoffFeedbackDistinguishesExpectedPeerEndFromCommittedFailure(bool silent)
    {
        MaintenanceFeedback.RequiresWindow(silent, true, new EndOfStreamException()).ShouldBeFalse();
        MaintenanceFeedback.RequiresWindow(silent, true, new OperationCanceledException()).ShouldBeFalse();
        MaintenanceFeedback.RequiresWindow(silent, true, new CommittedUpdateException(new IOException())).ShouldBe(!silent);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task EnvelopeAcceptsBoundedPayloadAndRejectsChangedBytes(bool corrupt)
    {
        using var temp = new TemporaryDirectory();
        byte[] payload = "synthetic payload"u8.ToArray();
        byte[] exe = new byte[8192 + payload.Length + InstallerEnvelope.FooterSize];
        payload.CopyTo(exe, 8192); int end = 8192 + payload.Length;
        InstallerEnvelope.Magic.CopyTo(exe.AsSpan(end));
        BinaryPrimitives.WriteInt64LittleEndian(exe.AsSpan(end + 16, 8), payload.Length);
        SHA256.HashData(payload).CopyTo(exe, end + 24);
        if (corrupt) exe[8192]++;
        string input = Path.Combine(temp.Path, "outer.exe"), output = Path.Combine(temp.Path, "payload.exe");
        await File.WriteAllBytesAsync(input, exe, TestContext.Current.CancellationToken);
        if (corrupt) await Should.ThrowAsync<InvalidDataException>(() => InstallerEnvelope.ExtractAsync(input, output, TestContext.Current.CancellationToken));
        else { await InstallerEnvelope.ExtractAsync(input, output, TestContext.Current.CancellationToken); (await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken)).ShouldBe(payload); }
    }
    [Fact]
    public void GateRemainsClosedAfterOwnerEndsUntilDurableTransactionIsResolved()
    {
        using var temp = new TemporaryDirectory();
        using (var gate = MaintenanceGate.Acquire(temp.Path))
        {
            gate.Write(new(Guid.NewGuid(), "update", "native-running", 123, 123, "1.1.0", false));
            MaintenanceGate.IsBlocked(temp.Path).ShouldBeTrue();
            Should.Throw<IOException>(() => MaintenanceGate.Acquire(temp.Path));
        }
        MaintenanceGate.IsBlocked(temp.Path).ShouldBeTrue();
        using (var recovery = MaintenanceGate.Acquire(temp.Path, true)) { recovery.Read()!.Phase.ShouldBe("native-running"); recovery.Complete(); }
        MaintenanceGate.IsBlocked(temp.Path).ShouldBeFalse();
    }
    [Fact]
    public async Task InstallWritesRegistrationOnlyAfterNativeSetupCompletes()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { HoldNative = true };
        var engine = new MaintenanceEngine(system); var work = engine.InstallAsync("owned-setup", false, TestContext.Current.CancellationToken);
        try
        {
            await system.NativeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            system.Registered.ShouldBeFalse(); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeTrue();
            system.NativeRelease.TrySetResult(); await work;
            system.Registered.ShouldBeTrue(); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeFalse();
            system.Starts.ShouldBe(0);
        }
        finally { system.NativeRelease.TrySetResult(); await work; }
    }
    [Fact]
    public async Task UninstalledDataFailureRetryNeverRunsNativeUninstallAgain()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { FailDelete = true };
        var engine = new MaintenanceEngine(system);
        await Should.ThrowAsync<IOException>(() => engine.UninstallAsync(true, TestContext.Current.CancellationToken));
        system.NativeCalls.ShouldBe(1); system.Uninstalled.ShouldBeTrue(); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeTrue();
        system.FailDelete = false; await engine.RecoverAsync(TestContext.Current.CancellationToken);
        system.Deleted.ShouldBeTrue(); system.NativeCalls.ShouldBe(1); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeFalse();
    }
    [Fact]
    public async Task NativeUninstallFailureKeepsDeleteIntentAndHasAnExecutableRetry()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { NativeExitCode = 5 };
        var engine = new MaintenanceEngine(system);
        await Should.ThrowAsync<IOException>(() => engine.UninstallAsync(true, TestContext.Current.CancellationToken));
        system.Deleted.ShouldBeFalse(); system.NativeCalls.ShouldBe(1);
        system.NativeExitCode = 0;
        await engine.RecoverAsync(TestContext.Current.CancellationToken);
        system.Deleted.ShouldBeTrue(); system.NativeCalls.ShouldBe(2); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task SilentUninstallCannotResumeRetainedDeletionButInteractiveRecoveryCan()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { FailDelete = true };
        var engine = new MaintenanceEngine(system);
        await Should.ThrowAsync<IOException>(() => engine.UninstallAsync(true, TestContext.Current.CancellationToken));
        system.FailDelete = false;
        await Should.ThrowAsync<IOException>(() => engine.UninstallAsync(false, TestContext.Current.CancellationToken, allowRetainedDelete: false));
        await Should.ThrowAsync<IOException>(() => engine.RecoverAsync(TestContext.Current.CancellationToken, allowRetainedDelete: false));
        system.Deleted.ShouldBeFalse(); system.NativeCalls.ShouldBe(1); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeTrue();
        await engine.RecoverAsync(TestContext.Current.CancellationToken);
        system.Deleted.ShouldBeTrue(); system.NativeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task PartialUninstallCanRepairMissingFilesThenResumeTheOriginalDeleteChoice()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { NativeExitCode = 5 };
        var engine = new MaintenanceEngine(system);
        await Should.ThrowAsync<IOException>(() => engine.UninstallAsync(true, TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(system.InstallRoot, "current", ReleaseIdentity.FileName));
        await Should.ThrowAsync<Iris.Core.Errors.IrisException>(() => engine.RecoverAsync(TestContext.Current.CancellationToken));
        system.NativeExitCode = 0;
        await Should.ThrowAsync<IOException>(() => engine.InstallAsync("owned-repair", false, TestContext.Current.CancellationToken, "1.0.0", repair: true));
        system.NativeCalls.ShouldBe(2); system.Registered.ShouldBeTrue(); system.Deleted.ShouldBeFalse(); system.Starts.ShouldBe(0);
        await engine.RecoverAsync(TestContext.Current.CancellationToken);
        system.NativeCalls.ShouldBe(3); system.Deleted.ShouldBeTrue(); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeFalse();
        await engine.RecoverAsync(TestContext.Current.CancellationToken); system.NativeCalls.ShouldBe(3);
    }

    [Fact]
    public async Task CancelledRepairOrRetryKeepsTheOriginalTransactionAndDeleteConsent()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { NativeExitCode = 5 };
        var engine = new MaintenanceEngine(system);
        await Should.ThrowAsync<IOException>(() => engine.UninstallAsync(true, TestContext.Current.CancellationToken));
        MaintenanceTransaction original;
        using (var gate = MaintenanceGate.Acquire(system.StateRoot, true)) original = gate.Read()!;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => engine.InstallAsync("owned-repair", false, canceled.Token, "1.0.0", repair: true));
        using (var gate = MaintenanceGate.Acquire(system.StateRoot, true)) gate.Read().ShouldBe(original);
        await Should.ThrowAsync<OperationCanceledException>(() => engine.RecoverAsync(canceled.Token));
        using (var gate = MaintenanceGate.Acquire(system.StateRoot, true)) gate.Read().ShouldBe(original);
        system.NativeCalls.ShouldBe(1); system.Deleted.ShouldBeFalse();
        system.NativeExitCode = 0; await engine.RecoverAsync(TestContext.Current.CancellationToken);
        system.NativeCalls.ShouldBe(2); system.Deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task UnknownNativeStartingDoesNotTrustOldIdentityButAllowsExplicitVerifiedRepair()
    {
        using var temp = new TemporaryDirectory(); var system = new SystemBoundary(temp.Path) { InstallVersion = "2.0.0" };
        var engine = new MaintenanceEngine(system);
        using (var gate = MaintenanceGate.Acquire(system.StateRoot))
            gate.Write(new(Guid.NewGuid(), "install", "native-starting", int.MaxValue, 0, "", false, WorkerPath: "owned-old-setup"));
        await Should.ThrowAsync<IOException>(() => engine.RecoverAsync(TestContext.Current.CancellationToken));
        system.Registered.ShouldBeFalse(); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeTrue();
        await engine.InstallAsync("owned-new-setup", false, TestContext.Current.CancellationToken, "2.0.0", repair: true);
        system.NativeCalls.ShouldBe(1); system.Registered.ShouldBeTrue(); MaintenanceGate.IsBlocked(system.StateRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task StartedCallbackFailureStillJoinsTheActualOwnedWorker()
    {
        string name = "Iris.Maintenance.Tests." + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".release");
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".entered");
        int pid = 0;
        string command = "$release = [Threading.EventWaitHandle]::OpenExisting('" + name + ".release'); $entered = [Threading.EventWaitHandle]::OpenExisting('" + name + ".entered'); $null = $entered.Set(); $null = $release.WaitOne(); $entered.Dispose(); $release.Dispose(); exit 0";
        var owner = new MaintenanceSystem().RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", command], (id, _) => { pid = id; throw new IOException("synthetic journal failure"); });
        try
        {
            (await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken)).ShouldBeTrue();
            owner.IsCompleted.ShouldBeFalse();
            using (var worker = System.Diagnostics.Process.GetProcessById(pid)) worker.HasExited.ShouldBeFalse();
            release.Set(); await Should.ThrowAsync<IOException>(() => owner);
            Should.Throw<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        }
        finally { release.Set(); try { await owner; } catch (IOException) { } }
    }
    private sealed class SystemBoundary : MaintenanceSystem
    {
        private readonly string _root;
        internal bool HoldNative, Registered, Uninstalled, FailDelete, Deleted;
        internal int NativeCalls, Starts;
        internal int NativeExitCode;
        internal string InstallVersion = "1.0.0";
        internal readonly TaskCompletionSource NativeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), NativeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SystemBoundary(string root)
        {
            _root = root; Directory.CreateDirectory(Path.Combine(InstallRoot, "current"));
            File.WriteAllText(Path.Combine(InstallRoot, "current", ReleaseIdentity.FileName), JsonSerializer.Serialize(new ReleaseIdentity("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, "1.0.0")));
        }
        internal override string InstallRoot => Path.Combine(_root, "install");
        internal override string StateRoot => Path.Combine(_root, "state");
        internal override string DataRoot => Path.Combine(_root, "data");
        internal override Task CloseApplicationsAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        internal override async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, Action<int, long> started)
        {
            NativeCalls++; started(int.MaxValue, 0); NativeEntered.TrySetResult(); if (HoldNative) await NativeRelease.Task;
            if (arguments.Contains("--uninstall")) Uninstalled = NativeExitCode == 0;
            else File.WriteAllText(Path.Combine(InstallRoot, "current", ReleaseIdentity.FileName), JsonSerializer.Serialize(new ReleaseIdentity("Iris", "win", "win-x64", ReleaseIdentity.OfficialRepository, InstallVersion)));
            return NativeExitCode;
        }
        internal override void EnsureNoNativeWorker(MaintenanceTransaction state) { }
        internal override Task ConfigureAsync() { Registered = true; return Task.CompletedTask; }
        internal override bool IsUninstalled => Uninstalled;
        internal override void RemoveAutoStart() { }
        internal override void DeleteData() { if (FailDelete) throw new IOException("synthetic delete failure"); Deleted = true; }
        internal override void StartApplication() => Starts++;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iris-maintenance-test-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
