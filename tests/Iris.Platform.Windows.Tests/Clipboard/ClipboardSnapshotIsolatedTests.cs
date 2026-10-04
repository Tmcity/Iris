using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class ClipboardSnapshotIsolatedTests
{
    [Fact]
    public async Task SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess()
    {
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Iris.Clipboard.IsolatedHelper/bin/Release/net10.0-windows10.0.19041.0/Iris.Clipboard.IsolatedHelper.exe"));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "--snapshots" }
            }
        };
        bool started = false;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        string output = "";
        string errors = "";
        int exitCode = -1;
        try
        {
            started = process.Start();
            started.ShouldBeTrue();
            stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            output = await stdout;
            errors = await stderr;
            exitCode = process.ExitCode;
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(); } catch (Exception ex) { cleanup.Add(ex); }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch (Exception ex) { cleanup.Add(ex); }
            }
            if (stdout != null) try { await stdout; } catch (Exception ex) { cleanup.Add(ex); }
            if (stderr != null) try { await stderr; } catch (Exception ex) { cleanup.Add(ex); }
        }
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
        if (exitCode == 2) Assert.Skip($"[ENVIRONMENT LIMITATION] Snapshot isolated helper skipped: {output.Trim()}");
        exitCode.ShouldBe(0, errors + output);
        output.ShouldContain("[SNAPSHOTS] PASS");
        output.ShouldContain("[ISOLATED_CLIPBOARD] PASS");
    }
}
