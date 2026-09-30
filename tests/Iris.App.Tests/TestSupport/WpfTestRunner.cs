using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Iris.App.Tests.TestSupport;

public static class WpfTestRunner
{
    private static readonly Thread StaThread;
    private static readonly TaskCompletionSource<Dispatcher> DispatcherTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static void RunStaThread()
    {
        EnsureApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        DispatcherTcs.SetResult(dispatcher);
        Dispatcher.Run();
    }

    public static async Task RunAsync(Func<Task> work)
    {
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
