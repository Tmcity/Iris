using System.Reflection;
using Iris.App.Hosting;
using Iris.App.Tests.TestSupport;
using Iris.Core.Actions;
using Iris.Core.Storage;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Serilog.Core;
using Shouldly;

namespace Iris.App.Tests.Hosting;

public sealed class IpcStartupTests
{
    [Theory]
    [InlineData("--action")][InlineData("--translate")][InlineData("--unknown")]
    [InlineData("--action", "1")][InlineData("--action", "Exit", "extra")]
    [InlineData("--action", "TranslateText")][InlineData("--translate", " ")]
    [InlineData("--action", "Exit", "--translate", "private")]
    [InlineData("--help", "--help")]
    public void InvalidArgumentsReturnTwoWithoutAcquiringIdentityOrInitializingAnything(params string[] args)
    {
        var runtime = new Runtime();
        Program.Run(args, runtime).ShouldBe(2);
        runtime.IdentityReads.ShouldBe(0); runtime.Acquires.ShouldBe(0);
        runtime.Paths.ShouldBe(0); runtime.Loggers.ShouldBe(0); runtime.Hosts.ShouldBe(0); runtime.Apps.ShouldBe(0); runtime.Forwards.ShouldBe(0);
        runtime.Messages.ShouldBe([UiText.CommandLineInvalid]);
    }
    [Theory][InlineData("--help")][InlineData("-h")]
    public void HelpNeverStartsOrForwards(string arg)
    {
        var runtime = new Runtime(); Program.Run([arg], runtime).ShouldBe(0);
        runtime.IdentityReads.ShouldBe(0); runtime.Hosts.ShouldBe(0); runtime.Messages.ShouldBe([UiText.CommandLineUsage]);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SecondaryBranchKeepsPayloadExactAndNeverInitializesUserResources(bool translate)
    {
        var runtime = new Runtime { Secondary = true };
        string text = "  中文 😀\r\n\"quote\" --action Exit  ";
        Program.Run(translate ? ["--translate", text] : [], runtime).ShouldBe(0);
        runtime.Command.ShouldBe(translate ? new(IrisAction.TranslateText, text) : new IpcCommand(IrisAction.InputTranslate));
        runtime.Paths.ShouldBe(0); runtime.Loggers.ShouldBe(0); runtime.Hosts.ShouldBe(0); runtime.Apps.ShouldBe(0); runtime.Forwards.ShouldBe(1);
    }
    [Theory][InlineData((int)IpcSendResult.Unconfirmed)][InlineData((int)IpcSendResult.Unavailable)][InlineData((int)IpcSendResult.Rejected)]
    public void SecondaryFailureUsesFixedMessageAndNeverRetries(int resultValue)
    {
        var result = (IpcSendResult)resultValue;
        var runtime = new Runtime { Secondary = true, Reply = result };
        Program.Run(["--translate", "private"], runtime).ShouldBe(1); runtime.Forwards.ShouldBe(1);
        runtime.Messages.Single().ShouldNotContain("private");
        if (result == IpcSendResult.Unconfirmed) runtime.Messages.ShouldBe([UiText.IpcUnconfirmed]);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void PrimaryOwnsMutexOnSameThread_EvenWhenHostAndLoggerCleanupThrow(bool failCleanup)
    {
        var runtime = new Runtime { FailCleanup = failCleanup };
        Program.Run([], runtime).ShouldBe(failCleanup ? 1 : 0);
        runtime.Paths.ShouldBe(1); runtime.Loggers.ShouldBe(1); runtime.Hosts.ShouldBe(1); runtime.Apps.ShouldBe(1);
        runtime.Command.ShouldBeNull(); runtime.Forwards.ShouldBe(0); runtime.LoggerCloses.ShouldBe(1);
        runtime.AcquireThread.ShouldBe(runtime.ReleaseThread); runtime.ReleaseThread.ShouldBe(Environment.CurrentManagedThreadId);
        runtime.StartedAt.ShouldBeLessThanOrEqualTo(runtime.AcquiredAt);
        if (failCleanup) runtime.FailureTypes.ShouldContain(nameof(IOException));
        using var reopened = SingleInstanceGuard.TryAcquire(runtime.Identity.MutexName); reopened.ShouldNotBeNull();
        typeof(Program).GetMethod(nameof(Program.Main))!.GetCustomAttribute<STAThreadAttribute>().ShouldNotBeNull();
    }
    [Fact]
    public void ParserAcceptsOnlyNamedDefinedActionsAndPreservesUnknownRegisteredStatusForDispatcher()
    {
        CommandLineParser.Parse(["--action", "sCreEnShotTranslate"]).Command!.Action.ShouldBe(IrisAction.ScreenshotTranslate);
        CommandLineParser.Parse(["--action", "ToggleFloatingBall"]).Kind.ShouldBe(CommandLineKind.Run);
        CommandLineParser.Parse(["--action", " Exit "]).Kind.ShouldBe(CommandLineKind.Invalid);
    }
    [Fact]
    public void FailureAfterLoggerCreationIsRecordedBeforeClosingWithSafeMetadataOnly()
    {
        var runtime = new Runtime { FailApplication = true };
        Program.Run(["--translate", "private payload"], runtime).ShouldBe(1);
        runtime.FailureTypes.ShouldBe([nameof(InvalidOperationException)]);
        runtime.Events.ShouldBe(["failure", "logger-close"]);
        runtime.Messages.ShouldBe([UiText.StartupFailed]);
    }
    [Fact]
    public async Task ActualSecondaryMutexBranchAndSyncBridgeForwardThroughOwnedRealPipe()
    {
        var runtime = new Runtime { UseRealPipe = true };
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                using var guard = SingleInstanceGuard.TryAcquire(runtime.Identity.MutexName);
                guard.ShouldNotBeNull(); entered.TrySetResult(); release.Wait();
                ended.TrySetResult();
            }
            catch (Exception ex) { entered.TrySetException(ex); ended.TrySetException(ex); }
        });
        owner.SetApartmentState(ApartmentState.STA); owner.Start();
        await using var server = new NamedPipeIpcServer(runtime.Identity, new IpcPipeFactory(), TimeProvider.System, NullLogger<NamedPipeIpcServer>.Instance);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await entered.Task; server.Start(command => { received.TrySetResult(command); return IpcReply.Accepted; });
            (await Task.Run(() => Program.Run(["--translate", "  exact\ntext  "], runtime))).ShouldBe(0);
            (await received.Task).ShouldBe(new(IrisAction.TranslateText, "  exact\ntext  "));
            runtime.Paths.ShouldBe(0); runtime.Loggers.ShouldBe(0); runtime.Hosts.ShouldBe(0); runtime.Apps.ShouldBe(0);
        }
        finally { release.Set(); await ended.Task; owner.Join(); }
    }
    private sealed class Runtime : StartupRuntime
    {
        internal readonly IpcIdentity Identity = new("Iris.Tests.Pipe." + Guid.NewGuid().ToString("N"), "Local\\Iris.Tests.Mutex." + Guid.NewGuid().ToString("N"));
        internal int IdentityReads, Acquires, Paths, Loggers, Hosts, Apps, Forwards, LoggerCloses, AcquireThread, ReleaseThread;
        internal bool Secondary, FailCleanup, UseRealPipe, FailApplication; internal IpcSendResult Reply = IpcSendResult.Accepted;
        internal long AcquiredAt, StartedAt;
        internal readonly List<string> FailureTypes = [], Events = [];
        internal IpcCommand? Command; internal readonly List<string> Messages = [];
        internal override IpcIdentity GetIdentity() { IdentityReads++; return Identity; }
        internal override IDisposable? Acquire(IpcIdentity identity)
        {
            Acquires++; if (Secondary) return null;
            AcquiredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            AcquireThread = Environment.CurrentManagedThreadId;
            var guard = SingleInstanceGuard.TryAcquire(identity.MutexName);
            return guard == null ? null : new Guard(guard, () => ReleaseThread = Environment.CurrentManagedThreadId);
        }
        internal override async Task<IpcSendResult> ForwardAsync(IpcIdentity identity, IpcCommand command)
        {
            Forwards++; Command = command;
            if (UseRealPipe)
            {
                await using var client = new NamedPipeIpcClient(identity, new IpcPipeFactory(), TimeProvider.System);
                return await client.RunAsync(command).ConfigureAwait(false);
            }
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); return Reply;
        }
        internal override IAppPaths CreatePaths() { Paths++; return Substitute.For<IAppPaths>(); }
        internal override void OpenLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch) => Loggers++;
        internal override IHost BuildHost(IpcIdentity identity, IpcCommand? command, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp)
        {
            Hosts++; StartedAt = startTimestamp; Command = command; var host = Substitute.For<IHost>();
            if (FailCleanup) host.When(h => h.Dispose()).Do(_ => throw new IOException("synthetic-host-cleanup"));
            return host;
        }
        internal override int RunApplication(IHost host) { Apps++; if (FailApplication) throw new InvalidOperationException("private payload"); return 0; }
        internal override void CloseLogger() { LoggerCloses++; Events.Add("logger-close"); if (FailCleanup) throw new IOException("synthetic-logger-cleanup"); }
        internal override void LogFailure(string exceptionType) { Events.Add("failure"); FailureTypes.Add(exceptionType); }
        internal override void Show(string message) => Messages.Add(message);
        private sealed class Guard(IDisposable guard, Action releasing) : IDisposable
        { public void Dispose() { releasing(); guard.Dispose(); } }
    }
}
