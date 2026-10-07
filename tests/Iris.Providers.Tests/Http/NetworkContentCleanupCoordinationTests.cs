using System.Net;
using System.Runtime.ExceptionServices;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Settings;
using Iris.Providers.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Iris.Providers.Tests.Http;

public sealed class NetworkContentCleanupCoordinationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Stop_JoinsOriginalDisposeOutcome_AndRetriesOnlyAfterRealFailure(bool failOnce, bool reenter, bool stopFirst)
    {
        var factory = new LeafFactory();
        var manager = new NetworkTransportManager(factory, new NetworkRouteResolver(), NullLogger<NetworkTransportManager>.Instance);
        var generation = Guid.NewGuid(); manager.Apply(new(), generation);
        var lease = manager.Acquire(HttpClientNames.Llm,
            new(generation, new("http://synthetic.example.test/"), ProxyMode.None, null, null, ProxyRouteKind.DirectByMode));
        var leaf = new GatedContent(failOnce);
        var content = manager.OwnContent(lease, leaf);
        lease.Dispose();
        Task? disposing = null, stop = null, nestedStop = null;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        if (reenter)
        {
            leaf.OnDisposing = () =>
            {
                content.Dispose();
                nestedStop = manager.StopAsync();
            };
        }
        try
        {
            if (stopFirst) stop = manager.StopAsync();
            disposing = Task.Run(content.Dispose, CancellationToken.None);
            // Set only after any reentrant callbacks return, then hold the original leaf Dispose open.
            await leaf.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            disposing.IsCompleted.ShouldBeFalse(); leaf.Calls.ShouldBe(1);

            stop = manager.StopAsync();
            if (reenter) stop.ShouldBeSameAs(nestedStop);
            manager.StopAsync().ShouldBeSameAs(stop);
            stop.IsCompleted.ShouldBeFalse();
            manager.ContentCount.ShouldBe(1); manager.HandlerCount.ShouldBe(1);
            factory.Handler.Disposed.ShouldBeFalse();

            // A second synchronous caller must not dispose the leaf again or synchronously join itself.
            content.Dispose();
            leaf.Calls.ShouldBe(1); stop.IsCompleted.ShouldBeFalse();
            leaf.Release.Set();
            if (failOnce)
            {
                (await Should.ThrowAsync<IrisException>(() => disposing)).Code.ShouldBe(ErrorCode.NetworkUnavailable);
                (await Should.ThrowAsync<IrisException>(() => stop)).Code.ShouldBe(ErrorCode.NetworkUnavailable);
                // The leaf fails only once. Retrying inside the original Stop would hide that failure.
                leaf.Calls.ShouldBe(1); leaf.Disposed.ShouldBeFalse();
                manager.ContentCount.ShouldBe(1); manager.HandlerCount.ShouldBe(1);
                var retry = manager.StopAsync(); retry.ShouldNotBeSameAs(stop);
                await retry;
                leaf.Calls.ShouldBe(2);
            }
            else
            {
                await disposing; await stop; leaf.Calls.ShouldBe(1);
            }
            leaf.Disposed.ShouldBeTrue(); factory.Handler.Disposed.ShouldBeTrue();
            manager.ContentCount.ShouldBe(0); manager.HandlerCount.ShouldBe(0);
            var finished = manager.StopAsync(); await finished; manager.StopAsync().ShouldBeSameAs(finished);
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            // Open first, then join every original owner/Stop before attempting cleanup on this fixture.
            leaf.Release.Set();
            if (disposing != null) await ObserveAsync(disposing, failOnce, cleanupErrors);
            foreach (var task in new[] { stop, nestedStop }.OfType<Task>().Distinct())
                await ObserveAsync(task, failOnce, cleanupErrors);
            leaf.OnDisposing = null;
            leaf.DisableFailure();
            try { content.Dispose(); lease.Dispose(); await manager.StopAsync(); }
            catch (Exception ex) { cleanupErrors.Add(ex); }
            try { manager.Dispose(); }
            catch (Exception ex) { cleanupErrors.Add(ex); }
            leaf.Release.Dispose();
        }
        if (primary != null && cleanupErrors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanupErrors.Insert(0, primary);
        if (cleanupErrors.Count != 0) throw new AggregateException(cleanupErrors);
    }

    private static async Task ObserveAsync(Task work, bool expectedFailure, List<Exception> errors)
    {
        try { await work; }
        catch (IrisException ex) when (expectedFailure && ex.Code == ErrorCode.NetworkUnavailable) { }
        catch (Exception ex) { errors.Add(ex); }
    }

    private sealed class GatedContent(bool failOnce) : HttpContent
    {
        private int _failNext = failOnce ? 1 : 0;
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal Action? OnDisposing { get; set; }
        internal void DisableFailure() => Interlocked.Exchange(ref _failNext, 0);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _calls);
                OnDisposing?.Invoke();
                Entered.TrySetResult();
                Release.Wait();
                if (Interlocked.Exchange(ref _failNext, 0) != 0) throw new IOException("Synthetic one-time cleanup failure.");
                Disposed = true;
            }
            base.Dispose(disposing);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
    }

    private sealed class LeafFactory : NetworkTransportFactory
    {
        internal LeafHandler Handler { get; } = new();
        internal override HttpMessageHandler Create(NetworkRoute route) => Handler;
    }
    private sealed class LeafHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cleanup must not send HTTP.");
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
