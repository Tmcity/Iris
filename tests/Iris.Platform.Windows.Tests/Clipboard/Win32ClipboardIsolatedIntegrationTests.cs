using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Iris.Clipboard.IsolatedHelper;
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
                process.Kill();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
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

        exitCode.ShouldBe(0);
        stdout.ShouldContain("[ISOLATED_CLIPBOARD] PASS");
    }

    [Fact]
    public void HelperProtocol_SuccessFlow_ExecutesActualCoordinator_RestoresInOrder_AndReturnsSuccess()
    {
        var env = new FakeIsolatedStationEnvironment();
        bool bodyExecuted = false;

        int exitCode = IsolatedStationCoordinator.Run(env, _ =>
        {
            bodyExecuted = true;
            return (true, null);
        });

        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeSuccess);
        bodyExecuted.ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] PASS")).ShouldBeTrue();

        // Verify restoration occurs strictly before reading final sequence
        int restoreStationIdx = env.CallLog.IndexOf("SetProcessWindowStation(100)");
        int finalSeqIdx = env.CallLog.LastIndexOf("GetClipboardSequenceNumber() -> 1000 (Station=100)");

        restoreStationIdx.ShouldBeGreaterThan(-1);
        finalSeqIdx.ShouldBeGreaterThan(-1);
        (restoreStationIdx < finalSeqIdx).ShouldBeTrue("恢复原窗口站必须严格发生在采样原站最终序列号之前");

        // Verify full setup, execution, and cleanup ordering
        env.CallLog.ShouldContain("CreateWindowStation(null, 00000001, 0000037F)");
        env.CallLog.ShouldContain("SetProcessWindowStation(200)");
        env.CallLog.ShouldContain("CreateDesktop(IrisIsolatedDesktop)");
        env.CallLog.ShouldContain("SetThreadDesktop(201)");
        env.CallLog.ShouldContain("SetThreadDesktop(101)");
        env.CallLog.ShouldContain("CloseDesktop(201)");
        env.CallLog.ShouldContain("SetProcessWindowStation(100)");
        env.CallLog.ShouldContain("CloseWindowStation(200)");
    }

    [Fact]
    public void HelperProtocol_OriginalStationSequenceChangedConcurrently_PassesWithDiagnostic()
    {
        var env = new FakeIsolatedStationEnvironment();
        bool bodyExecuted = false;

        int exitCode = IsolatedStationCoordinator.Run(env, _ =>
        {
            bodyExecuted = true;
            // Simulate external update to original interactive station clipboard while body is running
            env.OriginalStationSequence += 5;
            return (true, null);
        });

        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeSuccess);
        bodyExecuted.ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] DIAGNOSTIC: Interactive sequence number changed in original station")).ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] PASS")).ShouldBeTrue();

        // Restoration order must still hold
        int restoreStationIdx = env.CallLog.IndexOf("SetProcessWindowStation(100)");
        int finalSeqIdx = env.CallLog.LastIndexOf("GetClipboardSequenceNumber() -> 1005 (Station=100)");

        restoreStationIdx.ShouldBeGreaterThan(-1);
        finalSeqIdx.ShouldBeGreaterThan(-1);
        (restoreStationIdx < finalSeqIdx).ShouldBeTrue("恢复原窗口站必须严格发生在采样原站最终序列号之前");
    }

    [Fact]
    public void HelperProtocol_CreateWindowStationConflict_ReturnsSkip_AndRestoresCleanly()
    {
        var env = new FakeIsolatedStationEnvironment
        {
            CreateWindowStationErrorCode = 183 // ERROR_ALREADY_EXISTS
        };

        bool bodyExecuted = false;
        int exitCode = IsolatedStationCoordinator.Run(env, _ =>
        {
            bodyExecuted = true;
            return (true, null);
        });

        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeSkip);
        bodyExecuted.ShouldBeFalse("创建冲突时不得进入业务测试");
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] SKIP")).ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] PASS")).ShouldBeFalse();
    }

    [Fact]
    public void HelperProtocol_BodyFailure_PreservesFailure_AndRunsCompleteRestoration()
    {
        var env = new FakeIsolatedStationEnvironment();

        int exitCode = IsolatedStationCoordinator.Run(env, _ =>
        {
            return (false, "DIB pixel mismatch simulated");
        });

        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeFailure);
        env.LoggedLines.Any(l => l.Contains("FAIL: DIB pixel mismatch simulated")).ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] PASS")).ShouldBeFalse();

        // Restoration must still run completely
        env.CallLog.ShouldContain("SetThreadDesktop(101)");
        env.CallLog.ShouldContain("CloseDesktop(201)");
        env.CallLog.ShouldContain("SetProcessWindowStation(100)");
        env.CallLog.ShouldContain("CloseWindowStation(200)");
    }

    [Fact]
    public void HelperProtocol_RestorationFailure_ConvertsToFailure_AndLogsCleanupFail()
    {
        var env = new FakeIsolatedStationEnvironment
        {
            RestoreProcessWindowStationFails = true,
            RestoreProcessWindowStationError = 5 // Access denied
        };

        int exitCode = IsolatedStationCoordinator.Run(env, _ => (true, null));

        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeFailure, "清理或恢复失败严禁保留 PASS 退出码 0");
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] CLEANUP_FAIL: SetProcessWindowStation(originalStation) failed (error=5)")).ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] FAIL: Restoration or cleanup failed.")).ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] PASS")).ShouldBeFalse();
    }

    [Fact]
    public void HelperProtocol_SkipFlowWithRestorationFailure_ConvertsSkipToFailure()
    {
        var env = new FakeIsolatedStationEnvironment
        {
            CreateDesktopFails = true, // Setup aborts after station is set
            RestoreProcessWindowStationFails = true,
            RestoreProcessWindowStationError = 5
        };

        int exitCode = IsolatedStationCoordinator.Run(env, _ => (true, null));

        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeFailure, "环境跳过流程中若清理失败，严禁保留正常 SKIP 退出码 2");
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] CLEANUP_FAIL")).ShouldBeTrue();
        env.LoggedLines.Any(l => l.Contains("[ISOLATED_CLIPBOARD] FAIL: Restoration or cleanup failed.")).ShouldBeTrue();
    }

    [Fact]
    public void HelperProtocol_PrematureSampling_MutationIsRejected()
    {
        // Mutation demonstration:
        // If final sequence sampling is executed while still in isolated station (CurrentStation == 200),
        // it samples IsolatedStationSequence (2000) instead of OriginalStationSequence (1000).
        var env = new FakeIsolatedStationEnvironment();

        // Verify the environment's isolated station sequence differs from original station sequence
        env.SetProcessWindowStation(env.IsolatedStationHandle, out _);
        uint sampledBeforeRestore = env.GetClipboardSequenceNumber();
        sampledBeforeRestore.ShouldBe(2000u);

        env.SetProcessWindowStation(env.OriginalStationHandle, out _);
        uint sampledAfterRestore = env.GetClipboardSequenceNumber();
        sampledAfterRestore.ShouldBe(1000u);

        sampledBeforeRestore.ShouldNotBe(sampledAfterRestore, "变异验证：未恢复原站时的序列号必须与原站不同");
    }

    [Fact]
    public void HelperProtocol_SuppressedRestoreFailure_MutationIsRejected()
    {
        // Mutation demonstration:
        // When restore fails, any mutation attempting to suppress cleanup failures and return success
        // will be rejected by asserting exitCode == ExitCodeFailure and checking CLEANUP_FAIL log.
        var env = new FakeIsolatedStationEnvironment
        {
            RestoreThreadDesktopFails = true,
            RestoreThreadDesktopError = 6
        };

        int exitCode = IsolatedStationCoordinator.Run(env, _ => (true, null));
        exitCode.ShouldBe(IsolatedStationCoordinator.ExitCodeFailure);
        exitCode.ShouldNotBe(IsolatedStationCoordinator.ExitCodeSuccess, "变异验证：恢复失败绝不得被包装为成功");
        env.LoggedLines.Any(l => l.Contains("CLEANUP_FAIL")).ShouldBeTrue();
    }
}

internal sealed class FakeIsolatedStationEnvironment : IIsolatedStationEnvironment
{
    public nint OriginalStationHandle { get; set; } = 100;
    public nint OriginalDesktopHandle { get; set; } = 101;
    public nint IsolatedStationHandle { get; set; } = 200;
    public nint IsolatedDesktopHandle { get; set; } = 201;

    public uint OriginalStationSequence { get; set; } = 1000;
    public uint IsolatedStationSequence { get; set; } = 2000;

    public nint CurrentStation { get; set; } = 100;
    public nint CurrentDesktop { get; set; } = 101;

    public int CreateWindowStationErrorCode { get; set; }
    public bool CreateWindowStationFails { get; set; }

    public bool SetProcessWindowStationFails { get; set; }
    public int SetProcessWindowStationError { get; set; } = 5;

    public bool CreateDesktopFails { get; set; }
    public int CreateDesktopError { get; set; } = 5;

    public bool SetThreadDesktopFails { get; set; }
    public int SetThreadDesktopError { get; set; } = 5;

    public bool CloseDesktopFails { get; set; }
    public int CloseDesktopError { get; set; } = 5;

    public bool CloseWindowStationFails { get; set; }
    public int CloseWindowStationError { get; set; } = 5;

    public bool RestoreProcessWindowStationFails { get; set; }
    public int RestoreProcessWindowStationError { get; set; } = 5;

    public bool RestoreThreadDesktopFails { get; set; }
    public int RestoreThreadDesktopError { get; set; } = 5;

    public List<string> CallLog { get; } = [];
    public List<string> LoggedLines { get; } = [];

    public nint GetProcessWindowStation()
    {
        CallLog.Add($"GetProcessWindowStation() -> {CurrentStation}");
        return CurrentStation;
    }

    public nint GetThreadDesktop(uint threadId)
    {
        CallLog.Add($"GetThreadDesktop({threadId}) -> {CurrentDesktop}");
        return CurrentDesktop;
    }

    public uint GetCurrentThreadId() => 1234;

    public nint CreateWindowStation(string? name, uint flags, uint access, out int error)
    {
        CallLog.Add($"CreateWindowStation({name ?? "null"}, {flags:X8}, {access:X8})");
        error = CreateWindowStationErrorCode;
        if (CreateWindowStationFails || error == 183)
        {
            return 0;
        }
        return IsolatedStationHandle;
    }

    public bool SetProcessWindowStation(nint h, out int error)
    {
        CallLog.Add($"SetProcessWindowStation({h})");
        if (h == OriginalStationHandle && RestoreProcessWindowStationFails)
        {
            error = RestoreProcessWindowStationError;
            return false;
        }
        if (h != OriginalStationHandle && SetProcessWindowStationFails)
        {
            error = SetProcessWindowStationError;
            return false;
        }
        CurrentStation = h;
        error = 0;
        return true;
    }

    public nint CreateDesktop(string name, uint flags, uint access, out int error)
    {
        CallLog.Add($"CreateDesktop({name})");
        if (CreateDesktopFails)
        {
            error = CreateDesktopError;
            return 0;
        }
        error = 0;
        return IsolatedDesktopHandle;
    }

    public bool SetThreadDesktop(nint h, out int error)
    {
        CallLog.Add($"SetThreadDesktop({h})");
        if (h == OriginalDesktopHandle && RestoreThreadDesktopFails)
        {
            error = RestoreThreadDesktopError;
            return false;
        }
        if (h != OriginalDesktopHandle && SetThreadDesktopFails)
        {
            error = SetThreadDesktopError;
            return false;
        }
        CurrentDesktop = h;
        error = 0;
        return true;
    }

    public bool CloseDesktop(nint h, out int error)
    {
        CallLog.Add($"CloseDesktop({h})");
        if (CloseDesktopFails)
        {
            error = CloseDesktopError;
            return false;
        }
        error = 0;
        return true;
    }

    public bool CloseWindowStation(nint h, out int error)
    {
        CallLog.Add($"CloseWindowStation({h})");
        if (CloseWindowStationFails)
        {
            error = CloseWindowStationError;
            return false;
        }
        error = 0;
        return true;
    }

    public uint GetClipboardSequenceNumber()
    {
        uint seq = CurrentStation == IsolatedStationHandle ? IsolatedStationSequence : OriginalStationSequence;
        CallLog.Add($"GetClipboardSequenceNumber() -> {seq} (Station={CurrentStation})");
        return seq;
    }

    public void WriteLine(string message)
    {
        LoggedLines.Add(message);
    }
}
