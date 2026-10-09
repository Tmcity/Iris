using System.Diagnostics;
using Iris.App.Hosting;
using Iris.Core.Actions;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Hosting;
using Serilog.Core;

namespace Iris.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        long started = Stopwatch.GetTimestamp();
        return Run(args, new DesktopStartupRuntime(), started);
    }

    internal static int Run(string[] args, StartupRuntime runtime, long? startTimestamp = null)
    {
        long started = startTimestamp ?? Stopwatch.GetTimestamp();
        var request = CommandLineParser.Parse(args);
        if (request.Kind != CommandLineKind.Run)
        {
            runtime.Show(request.Kind == CommandLineKind.Help ? UiText.CommandLineUsage : UiText.CommandLineInvalid);
            return request.Kind == CommandLineKind.Help ? 0 : 2;
        }
        IDisposable? guard = null;
        IDisposable? languageRun = null;
        int exitCode = 1;
        try
        {
            var identity = runtime.GetIdentity();
            guard = runtime.Acquire(identity);
            if (guard != null)
            {
                languageRun = runtime.BeginPrimaryRun();
                exitCode = RunPrimary(runtime, identity, request.Command, started);
            }
            else
            {
                // Approved only here: second instance, before WPF/Host/data/log initialization.
                // Client I/O has a total deadline, no UI context, and fully joined cleanup.
                var result = runtime.ForwardAsync(identity, request.Command ?? new(IrisAction.InputTranslate)).GetAwaiter().GetResult();
                exitCode = result == IpcSendResult.Accepted ? 0 : 1;
                if (exitCode != 0) runtime.Show(result == IpcSendResult.Unconfirmed ? UiText.IpcUnconfirmed
                    : result == IpcSendResult.Rejected ? UiText.IpcRejected : UiText.IpcUnavailable);
            }
        }
        catch (Exception) { runtime.Show(UiText.StartupFailed); exitCode = 1; }
        finally
        {
            // Same Main thread releases the mutex, even when Host or logger cleanup throws.
            try { guard?.Dispose(); }
            catch (Exception) { runtime.Show(UiText.StartupFailed); exitCode = 1; }
            finally { languageRun?.Dispose(); }
        }
        return exitCode;
    }

    private static int RunPrimary(StartupRuntime runtime, IpcIdentity identity, IpcCommand? command, long startTimestamp)
    {
        IHost? host = null;
        bool loggerOpened = false;
        bool failed = false;
        int result = 1;
        try
        {
            var paths = runtime.CreatePaths();
            var levelSwitch = new LoggingLevelSwitch();
            runtime.OpenLogger(paths, levelSwitch); loggerOpened = true;
            host = runtime.BuildHost(identity, command, paths, levelSwitch, startTimestamp);
            result = runtime.RunApplication(host);
        }
        catch (Exception ex)
        {
            if (loggerOpened) runtime.LogFailure(ex.GetType().Name);
            failed = true;
        }
        finally
        {
            try { host?.Dispose(); }
            catch (Exception ex)
            {
                if (loggerOpened) runtime.LogFailure(ex.GetType().Name);
                failed = true;
            }
            finally
            {
                try { if (loggerOpened) runtime.CloseLogger(); }
                catch (Exception) { failed = true; }
            }
        }
        if (failed) { runtime.Show(UiText.StartupFailed); return 1; }
        return result;
    }
}
