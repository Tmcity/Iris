using Iris.App.Tests.Capture;
using Iris.Core.Platform;

internal static class StartupCloseChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("PEER_OVERLAY_CLOSED_DURING_REMAINING_WINDOW_SHOW_ENDS_SESSION", () => ScreenshotTestScope.Run(async f =>
        {
            f.Monitors = new MonitorInfo[]
            {
                new("one", new(0, 0, 320, 240), new(0, 0, 320, 240), 1, true),
                new("two", new(320, 0, 320, 240), new(320, 0, 320, 240), 1, false),
                new("three", new(640, 0, 320, 240), new(640, 0, 320, 240), 1, false)
            };
            using var caller = new CancellationTokenSource();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool actualClosed = false;
            f.Native.BeforeShow = () =>
            {
                if (f.Native.Windows.Count != 3) return;
                var peer = f.Native.Windows[1];
                peer.Close();
                actualClosed = peer.IsClosed && peer.Handle == 0;
                closed.TrySetResult();
            };
            var task = f.Track(f.Service.CaptureRegionAsync(Iris.Core.Presentation.ScreenshotPurpose.Translate, caller.Token), typeof(OperationCanceledException));
            Exception? primary = null;
            var cleanup = new List<Exception>();
            try
            {
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (!actualClosed) throw new InvalidOperationException("Owned peer did not actually close.");
                Console.WriteLine($"STARTUP_PEER_CLOSE_BEFORE_WAIT closed={actualClosed} windows={f.Native.Windows.Count} taskCompleted={task.IsCompleted}");
                var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine($"STARTUP_PEER_CLOSE closed={actualClosed} canceled={result == null} allClosed={f.Native.Windows.All(w => w.IsClosed)}");
                if (result != null || f.Native.Windows.Any(w => !w.IsClosed))
                    throw new InvalidOperationException("Unexpected setup close left an incomplete overlay session.");
            }
            catch (Exception ex) { primary = ex; }
            finally
            {
                f.Native.BeforeShow = null;
                try { caller.Cancel(); } catch (Exception ex) { cleanup.Add(ex); }
                try { await task; }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                catch (Exception ex) { cleanup.Add(ex); }
                Console.WriteLine($"STARTUP_PEER_CLOSE_AFTER_CLEANUP allClosed={f.Native.Windows.All(w => w.IsClosed)} taskCompleted={task.IsCompleted} cleanupErrors={cleanup.Count}");
            }
            if (primary != null && cleanup.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) cleanup.Insert(0, primary);
            if (cleanup.Count != 0) throw new AggregateException(cleanup);
        }));
    }
}
