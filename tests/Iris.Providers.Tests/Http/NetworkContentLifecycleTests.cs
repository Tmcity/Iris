using System.Net;
using System.Text;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Settings;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Iris.Providers.Tests.Http;

public sealed class NetworkContentLifecycleTests
{
    private static NetworkProxySnapshot Snapshot(Guid id) => new(id, new("http://origin.example.test/"), ProxyMode.None, null, null, ProxyRouteKind.DirectByMode);

    [Theory][InlineData("sync")][InlineData("async")][InlineData("token")]
    public async Task AcquiringStream_PerformsZeroReadsAndReturnsNonzeroBytesBeforeEof(string path)
    {
        var factory = new LeafFactory(); using var manager = Manager(factory); var id = Guid.NewGuid(); manager.Apply(new(), id);
        using var lease = manager.Acquire(HttpClientNames.Llm, Snapshot(id));
        using var body = new ContinuousBody(); using var raw = new StreamContent(body);
        var content = manager.OwnContent(lease, raw);
        try
        {
            Stream stream = path == "sync" ? content.ReadAsStream(TestContext.Current.CancellationToken)
                : path == "async" ? await ReadWithoutToken(content) : await content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            body.BytesRead.ShouldBe(0); content.Headers.ContentLength.ShouldBeNull();
            byte[] bytes = new byte[1];
            (await stream.ReadAsync(bytes, TestContext.Current.CancellationToken)).ShouldBe(1); bytes[0].ShouldBe((byte)'d');
            body.BytesRead.ShouldBe(1); body.End.Task.IsCompleted.ShouldBeFalse();
            stream.Dispose(); manager.ContentCount.ShouldBe(1);
            content.Dispose(); manager.ContentCount.ShouldBe(0);
        }
        finally { body.End.TrySetResult(); content.Dispose(); lease.Dispose(); await manager.StopAsync(); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task NoncooperativeReadOrCopy_DisposeAndStopMustWaitForRealOperation(bool copy)
    {
        var factory = new LeafFactory(); using var manager = Manager(factory); var id = Guid.NewGuid(); manager.Apply(new(), id);
        using var lease = manager.Acquire(HttpClientNames.Llm, Snapshot(id));
        using var body = new ContinuousBody { IgnoreCancellation = true }; using var raw = new StreamContent(body);
        var content = manager.OwnContent(lease, raw); using var destination = new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task? operation = null; Task? stop = null;
        try
        {
            if (copy) operation = content.CopyToAsync(destination, cancellation.Token);
            else
            {
                var stream = await content.ReadAsStreamAsync(cancellation.Token); byte[] bytes = new byte[128];
                (await stream.ReadAsync(bytes, cancellation.Token)).ShouldBeGreaterThan(0);
                operation = stream.ReadAsync(bytes, cancellation.Token).AsTask();
            }
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel(); operation.IsCompleted.ShouldBeFalse();
            Should.Throw<IrisException>(content.Dispose); lease.Dispose();
            stop = manager.StopAsync(); stop.IsCompleted.ShouldBeFalse(); body.Disposed.ShouldBeFalse();
            factory.Leaf.Disposed.ShouldBeFalse();
            body.End.TrySetResult(); await operation; await stop;
            body.Disposed.ShouldBeTrue(); factory.Leaf.Disposed.ShouldBeTrue(); manager.ContentCount.ShouldBe(0);
            if (copy) Encoding.UTF8.GetString(destination.ToArray()).ShouldBe("data: first\n\n");
        }
        finally
        {
            body.End.TrySetResult(); cancellation.Cancel();
            if (operation != null) await operation;
            content.Dispose(); lease.Dispose(); await (stop ?? manager.StopAsync());
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task CooperativeReadOrCopyCancellation_JoinsBeforeResponseLeaseRelease(bool copy)
    {
        var factory = new LeafFactory(); using var manager = Manager(factory); var id = Guid.NewGuid(); manager.Apply(new(), id);
        using var lease = manager.Acquire(HttpClientNames.Ocr, Snapshot(id));
        using var body = new ContinuousBody(); using var raw = new StreamContent(body); var content = manager.OwnContent(lease, raw);
        using var destination = new MemoryStream(); using var cancellation = new CancellationTokenSource(); Task? operation = null;
        try
        {
            if (copy) operation = content.CopyToAsync(destination, cancellation.Token);
            else
            {
                var stream = await content.ReadAsStreamAsync(cancellation.Token); byte[] bytes = new byte[128];
                (await stream.ReadAsync(bytes, cancellation.Token)).ShouldBeGreaterThan(0); operation = stream.ReadAsync(bytes, cancellation.Token).AsTask();
            }
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => operation);
            manager.ContentCount.ShouldBe(1); content.Dispose(); manager.ContentCount.ShouldBe(0);
        }
        finally
        {
            body.End.TrySetResult(); cancellation.Cancel();
            if (operation != null) try { await operation; } catch (OperationCanceledException) { }
            content.Dispose(); lease.Dispose(); await manager.StopAsync();
        }
    }

    [Fact]
    public async Task FailedStreamDispose_RetriesActualLeafAfterResponseHasMarkedItselfDisposed()
    {
        var factory = new LeafFactory(); using var manager = Manager(factory); var id = Guid.NewGuid(); manager.Apply(new(), id);
        using var lease = manager.Acquire(HttpClientNames.Llm, Snapshot(id));
        using var body = new ContinuousBody { FailDispose = true }; using var raw = new StreamContent(body);
        var content = manager.OwnContent(lease, raw); using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        try
        {
            await content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            Should.Throw<IrisException>(response.Dispose); lease.Dispose(); manager.ContentCount.ShouldBe(1);
            await Should.ThrowAsync<IrisException>(manager.StopAsync); manager.ContentCount.ShouldBe(1);
            int attempts = body.DisposeCalls; body.FailDispose = false;
            await manager.StopAsync(); body.DisposeCalls.ShouldBeGreaterThan(attempts); body.Disposed.ShouldBeTrue(); manager.ContentCount.ShouldBe(0);
        }
        finally { body.FailDispose = false; body.End.TrySetResult(); content.Dispose(); lease.Dispose(); await manager.StopAsync(); }
    }

    [Fact]
    public async Task FailedHandlerDispose_RetainsActualHandlerAndCapacityUntilRetry()
    {
        var factory = new LeafFactory(); using var manager = Manager(factory); var id = Guid.NewGuid(); manager.Apply(new(), id);
        var lease = manager.Acquire(HttpClientNames.Llm, Snapshot(id)); factory.Leaf.FailDispose = true;
        try
        {
            lease.Dispose(); await Should.ThrowAsync<IrisException>(manager.StopAsync);
            manager.HandlerCount.ShouldBe(1); factory.Leaf.Disposed.ShouldBeFalse();
            int attempts = factory.Leaf.DisposeCalls; factory.Leaf.FailDispose = false;
            await manager.StopAsync(); factory.Leaf.DisposeCalls.ShouldBeGreaterThan(attempts); manager.HandlerCount.ShouldBe(0);
        }
        finally { factory.Leaf.FailDispose = false; lease.Dispose(); await manager.StopAsync(); }
    }

    [Theory][InlineData(HttpClientNames.Llm)][InlineData(HttpClientNames.Ocr)]
    public async Task RetriedIntermediateContentDisposeFailure_ManagerRetainsItAfterLogicalSendEnds(string family)
    {
        var content = new FailContent { FailDispose = true }; var factory = new LeafFactory();
        factory.Leaf.Response = () => factory.Leaf.Sends == 1 ? new(HttpStatusCode.ServiceUnavailable) { Content = content } : new(HttpStatusCode.OK);
        var services = new ServiceCollection(); services.AddLogging(); DirectSnapshotSource.Register(services);
        services.AddSingleton<NetworkTransportFactory>(factory); services.AddIrisProviders();
        services.AddSingleton(NSubstitute.Substitute.For<ISettingsService>());
        services.AddSingleton(NSubstitute.Substitute.For<Iris.Core.Translation.IPromptTemplateProvider>());
        services.AddSingleton(NSubstitute.Substitute.For<Iris.Core.Translation.IPromptRenderer>());
        services.ConfigureAll<HttpStandardResilienceOptions>(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.UseJitter = false; });
        using var sp = services.BuildServiceProvider(); var manager = sp.GetRequiredService<NetworkTransportManager>();
        using var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(family);
        HttpResponseMessage? final = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://origin.example.test/");
            try { final = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken); }
            catch (IrisException) { /* A retry callback may propagate disposal failure. Ownership must survive either outcome. */ }
            final?.Dispose();
            manager.ContentCount.ShouldBe(1); factory.Leaf.Disposed.ShouldBeFalse();
            await Should.ThrowAsync<IrisException>(manager.StopAsync); int calls = factory.Leaf.Sends;
            content.FailDispose = false; await manager.StopAsync(); content.Disposed.ShouldBeTrue(); factory.Leaf.Sends.ShouldBe(calls);
        }
        finally { content.FailDispose = false; final?.Dispose(); await manager.StopAsync(); content.Dispose(); }
    }

    private static Task<Stream> ReadWithoutToken(HttpContent content) => content.ReadAsStreamAsync();

    private static NetworkTransportManager Manager(LeafFactory factory) => new(factory, new NetworkRouteResolver(), NullLogger<NetworkTransportManager>.Instance);
    private sealed class LeafFactory : NetworkTransportFactory
    {
        internal LeafHandler Leaf { get; } = new();
        internal override HttpMessageHandler Create(NetworkRoute route) => Leaf;
    }
    private sealed class LeafHandler : HttpMessageHandler
    {
        internal bool FailDispose, Disposed;
        internal int DisposeCalls, Sends;
        internal Func<HttpResponseMessage> Response = () => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Sends++; var result = Response(); result.Headers.RetryAfter = new(TimeSpan.Zero); return Task.FromResult(result); }
        protected override void Dispose(bool disposing)
        { if (disposing) { DisposeCalls++; if (FailDispose) throw new IOException("synthetic-leaf"); Disposed = true; } base.Dispose(disposing); }
    }
    private sealed class FailContent : HttpContent
    {
        internal bool FailDispose, Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { if (disposing) { if (FailDispose) throw new IOException("synthetic-content"); Disposed = true; } base.Dispose(disposing); }
    }
    private sealed class ContinuousBody : Stream
    {
        private readonly byte[] _bytes = "data: first\n\n"u8.ToArray();
        internal int BytesRead, DisposeCalls;
        internal bool IgnoreCancellation, FailDispose, Disposed;
        internal TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { int take = Math.Min(count, _bytes.Length - BytesRead); _bytes.AsSpan(BytesRead, take).CopyTo(buffer.AsSpan(offset)); BytesRead += take; return take; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BytesRead < _bytes.Length) { int take = Math.Min(buffer.Length, _bytes.Length - BytesRead); _bytes.AsMemory(BytesRead, take).CopyTo(buffer); BytesRead += take; return take; }
            Waiting.TrySetResult(); if (IgnoreCancellation) await End.Task; else await End.Task.WaitAsync(cancellationToken); return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) { DisposeCalls++; if (FailDispose) throw new IOException("synthetic-read-leaf"); Disposed = true; } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
