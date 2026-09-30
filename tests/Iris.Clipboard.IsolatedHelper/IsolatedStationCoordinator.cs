using System;
using System.Collections.Generic;

namespace Iris.Clipboard.IsolatedHelper;

internal interface IIsolatedStationEnvironment
{
    nint GetProcessWindowStation();
    nint GetThreadDesktop(uint threadId);
    uint GetCurrentThreadId();
    nint CreateWindowStation(string? name, uint flags, uint access, out int error);
    bool SetProcessWindowStation(nint h, out int error);
    nint CreateDesktop(string name, uint flags, uint access, out int error);
    bool SetThreadDesktop(nint h, out int error);
    bool CloseDesktop(nint h, out int error);
    bool CloseWindowStation(nint h, out int error);
    uint GetClipboardSequenceNumber();
    void WriteLine(string message);
}

internal static class IsolatedStationCoordinator
{
    public const int ExitCodeSuccess = 0;
    public const int ExitCodeFailure = 1;
    public const int ExitCodeSkip = 2;

    public const uint CWF_CREATE_ONLY = 0x00000001;
    public const uint WINSTA_ALL_ACCESS = 0x0000037F;
    public const uint DESKTOP_ALL_ACCESS = 0x000001FF;

    public static int Run(
        IIsolatedStationEnvironment env,
        Func<IIsolatedStationEnvironment, (bool Success, string? ErrorMessage)> runBody)
    {
        var originalStation = env.GetProcessWindowStation();
        var originalDesktop = env.GetThreadDesktop(env.GetCurrentThreadId());
        var originalInteractiveSeq = env.GetClipboardSequenceNumber();

        nint station = 0;
        nint desktop = 0;

        int exitCode = ExitCodeFailure;
        bool setupSucceeded = false;
        bool bodySucceeded = false;
        string? failureMessage = null;

        try
        {
            station = env.CreateWindowStation(null, CWF_CREATE_ONLY, WINSTA_ALL_ACCESS, out int createStationError);
            if (station == 0 || createStationError == 183 || station == originalStation)
            {
                env.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station={station}, error={createStationError})");
                exitCode = ExitCodeSkip;
            }
            else if (!env.SetProcessWindowStation(station, out int setStationError))
            {
                env.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: SetProcessWindowStation failed (error={setStationError})");
                exitCode = ExitCodeSkip;
            }
            else if ((desktop = env.CreateDesktop("IrisIsolatedDesktop", 0, DESKTOP_ALL_ACCESS, out int createDesktopError)) == 0)
            {
                env.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: CreateDesktop failed (error={createDesktopError})");
                exitCode = ExitCodeSkip;
            }
            else if (!env.SetThreadDesktop(desktop, out int setDesktopError))
            {
                env.WriteLine($"[ISOLATED_CLIPBOARD] SKIP: SetThreadDesktop failed (error={setDesktopError})");
                exitCode = ExitCodeSkip;
            }
            else
            {
                setupSucceeded = true;
                env.WriteLine("[ISOLATED_CLIPBOARD] Isolated window station & desktop successfully established.");

                var (success, errorMsg) = runBody(env);
                if (!success)
                {
                    bodySucceeded = false;
                    failureMessage = errorMsg ?? "Body execution failed";
                    exitCode = ExitCodeFailure;
                }
                else
                {
                    bodySucceeded = true;
                }
            }
        }
        catch (Exception ex)
        {
            bodySucceeded = false;
            failureMessage = ex.ToString();
            exitCode = ExitCodeFailure;
        }
        finally
        {
            bool cleanupFailed = false;
            var cleanupErrors = new List<string>();

            if (originalDesktop != 0)
            {
                if (!env.SetThreadDesktop(originalDesktop, out int err))
                {
                    cleanupFailed = true;
                    cleanupErrors.Add($"SetThreadDesktop(originalDesktop) failed (error={err})");
                }
            }
            if (desktop != 0)
            {
                if (!env.CloseDesktop(desktop, out int err))
                {
                    cleanupFailed = true;
                    cleanupErrors.Add($"CloseDesktop(isolatedDesktop) failed (error={err})");
                }
                desktop = 0;
            }
            if (originalStation != 0)
            {
                if (!env.SetProcessWindowStation(originalStation, out int err))
                {
                    cleanupFailed = true;
                    cleanupErrors.Add($"SetProcessWindowStation(originalStation) failed (error={err})");
                }
            }
            if (station != 0 && station != originalStation)
            {
                if (!env.CloseWindowStation(station, out int err))
                {
                    cleanupFailed = true;
                    cleanupErrors.Add($"CloseWindowStation(isolatedStation) failed (error={err})");
                }
                station = 0;
            }

            if (cleanupFailed)
            {
                foreach (var err in cleanupErrors)
                {
                    env.WriteLine($"[ISOLATED_CLIPBOARD] CLEANUP_FAIL: {err}");
                }

                if (exitCode == ExitCodeSuccess || exitCode == ExitCodeSkip || setupSucceeded)
                {
                    env.WriteLine("[ISOLATED_CLIPBOARD] FAIL: Restoration or cleanup failed.");
                    exitCode = ExitCodeFailure;
                }
                if (failureMessage != null)
                {
                    env.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: {failureMessage}");
                }
            }
            else if (setupSucceeded && bodySucceeded)
            {
                uint finalInteractiveSeq = env.GetClipboardSequenceNumber();
                if (finalInteractiveSeq != originalInteractiveSeq)
                {
                    env.WriteLine($"[ISOLATED_CLIPBOARD] DIAGNOSTIC: Interactive sequence number changed in original station (original={originalInteractiveSeq}, restored={finalInteractiveSeq})");
                }

                env.WriteLine($"[ISOLATED_CLIPBOARD] PASS (originalSeq={originalInteractiveSeq}, restoredSeq={finalInteractiveSeq})");
                exitCode = ExitCodeSuccess;
            }
            else if (!bodySucceeded && setupSucceeded)
            {
                if (failureMessage != null)
                {
                    env.WriteLine($"[ISOLATED_CLIPBOARD] FAIL: {failureMessage}");
                }
                exitCode = ExitCodeFailure;
            }
        }

        return exitCode;
    }
}
