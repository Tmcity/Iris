using System.Runtime.ExceptionServices;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class FakeUiaProcessSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitAndDispose_JoinRegisteredResponseBeforePublishingExit(bool kill)
    {
        var session = new FakeUiaProcessSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task>();
        Exception? primary = null;
        var errors = new List<Exception>();
        try
        {
            tasks.Add(session.StartResponse(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                session.EnqueueReady();
                session.SignalExit();
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (kill) session.Kill(); else session.SignalExit();
            session.HasExited.ShouldBeFalse();
            var wait = session.WaitForExitAsync(CancellationToken.None); tasks.Add(wait);
            var dispose = session.DisposeAsync().AsTask(); tasks.Add(dispose);
            wait.IsCompleted.ShouldBeFalse(); dispose.IsCompleted.ShouldBeFalse();
            session.Disposed.ShouldBeFalse();
            var repeatedExit = Task.Run(() => { session.Kill(); session.SignalExit(); }, TestContext.Current.CancellationToken); tasks.Add(repeatedExit);
            await repeatedExit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            release.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            session.HasExited.ShouldBeTrue(); session.Disposed.ShouldBeTrue();
            await session.DisposeAsync();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            release.TrySetResult();
            foreach (var task in tasks)
                try { await task; } catch (Exception ex) { errors.Add(ex); }
            try { await session.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
