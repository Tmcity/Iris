using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Iris.App.Tests.TestSupport;

public static class WpfTestRunner
{
    private static readonly Thread StaThread;
    private static readonly TaskCompletionSource<Dispatcher> DispatcherTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Lock Gate = new();
    private static bool _isShuttingDown;
    private static Task? _teardownTask;

    static WpfTestRunner()
    {
        StaThread = new Thread(RunStaThread)
        {
            IsBackground = true,
            Name = "Iris-App-WpfTests-STA"
        };
        StaThread.SetApartmentState(ApartmentState.STA);
        StaThread.Start();
    }

    public static bool IsStaThreadAlive => StaThread.IsAlive;

    private static void RunStaThread()
    {
        try
        {
            EnsureApplication();
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            DispatcherTcs.SetResult(dispatcher);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            DispatcherTcs.TrySetException(ex);
        }
    }

    public static async Task RunAsync(Func<Task> work)
    {
        lock (Gate)
        {
            if (_isShuttingDown)
            {
                throw new InvalidOperationException("WPF test runner is shutting down or has completed.");
            }
        }

        var dispatcher = await DispatcherTcs.Task.ConfigureAwait(false);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await work().ConfigureAwait(true);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        await tcs.Task.ConfigureAwait(false);
    }

    public static async Task RunAsync(Action work)
    {
        lock (Gate)
        {
            if (_isShuttingDown)
            {
                throw new InvalidOperationException("WPF test runner is shutting down or has completed.");
            }
        }

        var dispatcher = await DispatcherTcs.Task.ConfigureAwait(false);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                work();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        await tcs.Task.ConfigureAwait(false);
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        lock (Gate)
        {
            if (_isShuttingDown)
            {
                throw new InvalidOperationException("WPF test runner is shutting down or has completed.");
            }
        }

        var dispatcher = await DispatcherTcs.Task.ConfigureAwait(false);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var result = await work().ConfigureAwait(true);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    public static async Task CleanupWindowsAsync()
    {
        var dispatcher = await DispatcherTcs.Task.ConfigureAwait(false);
        if (!dispatcher.HasShutdownStarted)
        {
            await dispatcher.InvokeAsync(CleanupWindowsCore);
        }
    }

    private static void CleanupWindowsCore()
    {
        if (Application.Current != null)
        {
            foreach (var window in Application.Current.Windows.Cast<Window>().ToArray())
            {
                try
                {
                    if (window is Iris.App.Settings.SettingsWindow sw)
                    {
                        sw.ForceClose = true;
                    }
                    window.Close();
                }
                catch
                {
                }
            }
        }
    }

    public static Task TeardownAsync(TimeSpan timeout = default)
    {
        lock (Gate)
        {
            if (_teardownTask != null)
            {
                return _teardownTask;
            }
            _isShuttingDown = true;
            _teardownTask = TeardownCoreAsync(timeout);
            return _teardownTask;
        }
    }

    private static async Task TeardownCoreAsync(TimeSpan timeout)
    {
        int timeoutMs = timeout > TimeSpan.Zero ? (int)timeout.TotalMilliseconds : 5000;

        if (DispatcherTcs.Task.IsCompletedSuccessfully)
        {
            var dispatcher = DispatcherTcs.Task.Result;
            if (!dispatcher.HasShutdownStarted)
            {
                try
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        CleanupWindowsCore();
                        try
                        {
                            Application.Current?.Shutdown();
                        }
                        catch
                        {
                        }
                    }).Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                }

                dispatcher.InvokeShutdown();
            }
        }

        bool exited = await Task.Run(() => StaThread.Join(timeoutMs)).ConfigureAwait(false);
        if (!exited)
        {
            throw new TimeoutException($"WPF STA 线程未能于 {timeoutMs}ms 内退出。");
        }
    }

    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        }
    }
}
