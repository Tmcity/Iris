using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Clipboard;

public sealed class Win32ClipboardIsolatedIntegrationTests
{
    [Fact]
    public async Task IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess()
    {
        string baseDir = AppContext.BaseDirectory;
        // Search for helper exe
        string helperPath = Path.Combine(baseDir, "..", "..", "..", "..", "Iris.Clipboard.IsolatedHelper", "bin", "Release", "net10.0-windows10.0.19041.0", "Iris.Clipboard.IsolatedHelper.exe");
        helperPath = Path.GetFullPath(helperPath);

        if (!File.Exists(helperPath))
        {
            // Fallback: check in baseDir
            string localHelper = Path.Combine(baseDir, "Iris.Clipboard.IsolatedHelper.exe");
            if (File.Exists(localHelper))
            {
                helperPath = localHelper;
            }
            else
            {
                throw new FileNotFoundException($"Cannot locate Iris.Clipboard.IsolatedHelper.exe at '{helperPath}' or '{localHelper}'.");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort process cleanup
            }
            throw new TimeoutException("Iris.Clipboard.IsolatedHelper exceeded 15 seconds timeout.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        int exitCode = process.ExitCode;

        if (exitCode == 2)
        {
            // Exit code 2 represents SKIP due to environment restriction (CWF_CREATE_ONLY returned 183 or similar)
            string skipReason = stdout.Trim();
            Assert.Skip($"[ENVIRONMENT LIMITATION] Native isolated window station test skipped: {skipReason}");
            return;
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Isolated helper failed with exit code {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        stdout.ShouldContain("[ISOLATED_CLIPBOARD] PASS");
    }
}
