using System.Collections.Concurrent;

namespace Iris.Core.Tests.TestSupport;

/// <summary>Runs an async test controller on its own joined thread; production work keeps its real scheduler.</summary>
internal static class OwnedAsyncTestDriver
{
    internal static async Task RunAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new Thread(() =>
        {
            using var context = new DriverContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var work = body();
                while (!work.IsCompleted) context.RunNext();
                work.GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true, Name = "Iris owned async test driver" };
        driver.Start();
        Exception? failure = null;
        try { await completion.Task.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        bool joined = driver.Join(TimeSpan.FromSeconds(5));
        if (!joined)
        {
            var joinFailure = new InvalidOperationException("Owned test driver did not exit.");
            if (failure != null) throw new AggregateException(failure, joinFailure);
            throw joinFailure;
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class DriverContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        public override SynchronizationContext CreateCopy() => this;
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        internal void RunNext()
        {
            if (_queue.TryTake(out var callback, 100)) callback();
        }
        public void Dispose() => _queue.Dispose();
    }
}
