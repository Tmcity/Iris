using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Platform.Windows.Selection;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class IrisUiaHostProcessSmokeTests
{
    private static string GetHostExecutablePath()
    {
        var currentDir = AppContext.BaseDirectory;
        var candidate1 = Path.Combine(currentDir, "uia", "Iris.UiaHost.exe");
        if (File.Exists(candidate1)) return candidate1;

        var parent = Directory.GetParent(currentDir);
        var configDir = parent?.Parent?.Name;
        var tfm = parent?.Name;
        if (configDir != null && tfm != null)
        {
            var repoRoot = Path.GetFullPath(Path.Combine(currentDir, "../../../../../"));
            var candidate2 = Path.Combine(repoRoot, "src", "Iris.UiaHost", "bin", configDir, tfm, "Iris.UiaHost.exe");
            if (File.Exists(candidate2)) return candidate2;
        }

        throw new FileNotFoundException($"Cannot locate Iris.UiaHost.exe from '{currentDir}'.");
    }

    [Fact]
    public async Task HostProcess_Starts_SendsReadyFrame_AndExitsCleanlyOnStdinClose()
    {
        var hostPath = GetHostExecutablePath();
        var runner = new RealUiaProcessRunner(hostPath);

        await using var session = await runner.StartAsync(TestContext.Current.CancellationToken);

        var readyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
            session.StandardOutput,
            UiaProtocolConstants.MaxFrameBytes,
            TestContext.Current.CancellationToken);

        readyLine.ShouldNotBeNull();
        var ready = UiaProtocolCodec.DeserializeReady(readyLine);
        ready.ShouldNotBeNull();
        ready.Kind.ShouldBe("Ready");
        ready.ProtocolVersion.ShouldBe(UiaProtocolConstants.CurrentVersion);

        // Close stdin (EOF) without sending request
        session.StandardInput.Close();

        var remainingOutput = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
            session.StandardOutput,
            UiaProtocolConstants.MaxFrameBytes,
            TestContext.Current.CancellationToken);

        using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await session.WaitForExitAsync(exitCts.Token);

        remainingOutput.ShouldBeNull();
        session.HasExited.ShouldBeTrue();
        session.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task HostProcess_WhenInvalidRequestSent_RepliesFailedAndExitsWithNonZeroCode()
    {
        var hostPath = GetHostExecutablePath();
        var runner = new RealUiaProcessRunner(hostPath);

        await using var session = await runner.StartAsync(TestContext.Current.CancellationToken);

        var readyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
            session.StandardOutput,
            UiaProtocolConstants.MaxFrameBytes,
            TestContext.Current.CancellationToken);
        readyLine.ShouldNotBeNull();

        // Send invalid JSON line
        var badPayload = Encoding.UTF8.GetBytes("{ \"InvalidJson\": true }\n");
        await session.StandardInput.WriteAsync(badPayload, TestContext.Current.CancellationToken);
        await session.StandardInput.FlushAsync(TestContext.Current.CancellationToken);

        var replyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
            session.StandardOutput,
            UiaProtocolConstants.MaxFrameBytes,
            TestContext.Current.CancellationToken);

        replyLine.ShouldNotBeNull();
        var reply = UiaProtocolCodec.DeserializeReply(replyLine);
        reply.ShouldNotBeNull();
        reply.Status.ShouldBe(UiaSelectionStatus.Failed);
        reply.ErrorCode.ShouldBe("INVALID_REQUEST_FRAME");

        using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await session.WaitForExitAsync(exitCts.Token);

        session.HasExited.ShouldBeTrue();
        session.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task HostProcess_WhenKilledWithoutRequest_TerminatesImmediately()
    {
        var hostPath = GetHostExecutablePath();
        var runner = new RealUiaProcessRunner(hostPath);

        await using var session = await runner.StartAsync(TestContext.Current.CancellationToken);

        var readyLine = await UiaProtocolFrameReader.ReadLineWithLimitAsync(
            session.StandardOutput,
            UiaProtocolConstants.MaxFrameBytes,
            TestContext.Current.CancellationToken);
        readyLine.ShouldNotBeNull();

        session.Kill();

        using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await session.WaitForExitAsync(exitCts.Token);

        session.HasExited.ShouldBeTrue();
    }
}
