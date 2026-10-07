using System.Net;
using System.Reflection;
using System.Text;
using Iris.Providers.Http;
using Iris.Providers.DependencyInjection;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

internal static class StreamingChecks
{
    internal static HttpContent Wrap(HttpContent content, IDisposable lease) => (HttpContent)Activator.CreateInstance(
        typeof(CaptureHandler).GetNestedType("LeaseContent", BindingFlags.NonPublic)!,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [content, lease], null)!;

    internal static async Task Run()
    {
        await FirstBytesBeforeEof();
        await ReadModes();
        await RuntimePipelines();
        DisposalFailureRetry();
    }
    private static async Task FirstBytesBeforeEof()
    {
        var source = new ContinuousBody(); var lease = new CounterLease();
        using var wrapper = Wrap(new StreamContent(source), lease); Task<Stream>? acquiring = null; Stream? stream = null;
        try
        {
            acquiring = wrapper.ReadAsStreamAsync();
            await Task.WhenAny(acquiring, source.ReadWaiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"STREAM ReadAsStreamCompletedBeforeEof={acquiring.IsCompleted}; UnderlyingReadWaiting={source.ReadWaiting.Task.IsCompleted}");
            Check.That(acquiring.IsCompletedSuccessfully && source.BytesRead == 0, "ReadAsStreamAsync must return stream without consuming body or waiting for EOF");
            stream = await acquiring;
            byte[] first = new byte[1]; Check.That(await stream.ReadAsync(first) == 1 && first[0] == (byte)'d', "first byte must be available before EOF");
            Check.That(lease.Returns == 0, "response owns lease while stream is consumed");
            Check.Pass("stream acquisition consumes zero body bytes; first byte arrives before EOF");
        }
        finally
        {
            source.Finish.Open();
            if (acquiring != null) stream = await acquiring;
            if (stream != null) await stream.DisposeAsync();
            wrapper.Dispose();
            Console.WriteLine($"STREAM_CLEANUP sourceDisposed={source.Disposed} leaseReturns={lease.Returns}");
        }
        Check.That(lease.Returns == 1, "one lease return after disposal");
    }
    private static async Task ReadModes()
    {
        foreach (string mode in new[] { "async-token", "sync", "cancel", "noncooperative-cancel", "read-error", "copy", "copy-cancel" })
        {
            var source = new ContinuousBody { FailRead = mode == "read-error", IgnoreCancellation = mode == "noncooperative-cancel" }; var lease = new CounterLease();
            using var wrapper = Wrap(new StreamContent(source), lease); using var ct = new CancellationTokenSource();
            Stream? stream = null; Task? pending = null; IAsyncEnumerator<SseEvent>? enumerator = null; ObservedWriteStream? destination = null;
            try
            {
                if (mode is "copy" or "copy-cancel")
                {
                    destination = new ObservedWriteStream();
                    pending = wrapper.CopyToAsync(destination, ct.Token);
                    await destination.Wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check.That(!pending.IsCompleted && destination.Length > 0 && lease.Returns == 0, "CopyToAsync forwards bytes before EOF without returning response lease");
                    if (mode == "copy-cancel")
                    {
                        ct.Cancel();
                        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Copy cancellation was not forwarded."); }
                        catch (OperationCanceledException) { }
                    }
                    else { source.Finish.Open(); await pending; }
                }
                else
                {
                    stream = mode == "sync" ? wrapper.ReadAsStream(ct.Token) : await wrapper.ReadAsStreamAsync(ct.Token);
                    Check.That(source.BytesRead == 0, "get-stream overload must not buffer body");
                    var method = typeof(ProviderJson).Assembly.GetType("Iris.Providers.Http.SseParser")!.GetMethod("ReadEventsAsync")!;
                    var events = (IAsyncEnumerable<SseEvent>)method.Invoke(null, [stream, ct.Token, 1024L * 1024])!;
                    enumerator = events.GetAsyncEnumerator(ct.Token);
                    var first = enumerator.MoveNextAsync().AsTask(); pending = first;
                    await Task.WhenAny(first, source.ReadWaiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
                    Check.That(first.IsCompletedSuccessfully && await first && enumerator.Current.Data == "first", "actual Iris SSE parser first event must precede EOF");
                    Check.That(lease.Returns == 0, "SSE consumer still holds response ownership");
                    pending = enumerator.MoveNextAsync().AsTask();
                    if (mode is "cancel" or "noncooperative-cancel")
                    {
                        await source.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); ct.Cancel();
                        if (mode == "noncooperative-cancel") { Check.That(!pending.IsCompleted && lease.Returns == 0, "noncooperative read retains response lease until actual exit"); source.Finish.Open(); }
                    }
                    else source.Finish.Open();
                    try { await pending; Check.That(mode is not "cancel" and not "read-error", "expected canceled/faulted read"); }
                    catch (OperationCanceledException) when (mode is "cancel" or "noncooperative-cancel") { }
                    catch (IOException) when (mode == "read-error") { }
                }
            }
            finally
            {
                source.Finish.Open();
                if (pending != null) try { await pending; } catch (OperationCanceledException) when (mode is "cancel" or "noncooperative-cancel" or "copy-cancel") { } catch (IOException) when (mode == "read-error") { }
                if (enumerator != null) await enumerator.DisposeAsync();
                if (stream != null) await stream.DisposeAsync();
                Check.That(lease.Returns == 0, "borrowed stream disposal is conservative: response still owns lease");
                wrapper.Dispose();
                destination?.Dispose();
            }
            Check.That(lease.Returns == 1 && source.Disposed, "read failure/cancel/dispose must return response lease once");
            Check.Pass("non-buffered stream " + mode + ": actual SSE/copy before EOF and joined disposal");
        }
    }
    private static async Task RuntimePipelines()
    {
        foreach (string name in new[] { HttpClientNames.Llm, HttpClientNames.Ocr })
        foreach (string ending in new[] { "eof", "cancel", "read-error" })
        {
            var a = new ManagedSnapshot(Guid.NewGuid(), new(ProxyMode.Custom, "http://proxy.example.test:8080/", false), null);
            var b = a with { Version = Guid.NewGuid() }; var current = a;
            var wire = new ProxyWire(requireAuth: false, firstChunk: "data: first\n\n"); using var pools = new GenerationPool(wire);
            var capture = new CaptureHandler(() => current, pools, new WebProxy()); var router = new RoutingHandler(() => current = b);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ISettingsService>(new MemorySettings()); services.AddIrisProviders();
            services.Configure<HttpClientFactoryOptions>(name, options => options.HttpMessageHandlerBuilderActions.Add(builder =>
            { builder.PrimaryHandler.Dispose(); builder.PrimaryHandler = router; builder.AdditionalHandlers.Insert(0, capture); }));
            using var provider = services.BuildServiceProvider(); using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            using var token = new CancellationTokenSource(); HttpResponseMessage? first = null, next = null; Stream? body = null;
            IAsyncEnumerator<SseEvent>? parser = null; Task<bool>? reading = null;
            try
            {
                first = await client.SendAsync(RuntimeChecks.Request(), HttpCompletionOption.ResponseHeadersRead);
                var streamTask = first.Content.ReadAsStreamAsync(token.Token);
                Check.That(streamTask.IsCompletedSuccessfully, "actual chunked response must expose stream before EOF"); body = await streamTask;
                Check.That(first.Content.Headers.ContentType?.MediaType == "text/event-stream", "content headers forwarded");
                var method = typeof(ProviderJson).Assembly.GetType("Iris.Providers.Http.SseParser")!.GetMethod("ReadEventsAsync")!;
                parser = ((IAsyncEnumerable<SseEvent>)method.Invoke(null, [body, token.Token, 1024L * 1024])!).GetAsyncEnumerator(token.Token);
                reading = parser.MoveNextAsync().AsTask(); Check.That(await reading.WaitAsync(TimeSpan.FromSeconds(5)) && parser.Current.Data == "first", "first actual SSE event before chunked EOF");
                Check.That(router.Versions.Take(2).All(id => id == a.Version), "retry still uses A context for streaming response");
                next = await client.SendAsync(RuntimeChecks.Request(), HttpCompletionOption.ResponseHeadersRead);
                Check.That(!pools.Generations[0].Disposed && pools.Generations[0].Retired && pools.Generations[0].Users == 1, "old pool cannot retire while SSE is active");
                var source = wire.Streams.First(); source.EmitChunk("data: second\n\n");
                reading = parser.MoveNextAsync().AsTask(); Check.That(await reading.WaitAsync(TimeSpan.FromSeconds(5)) && parser.Current.Data == "second", "second SSE event progresses before EOF");
                reading = parser.MoveNextAsync().AsTask();
                if (ending == "eof") source.EndChunks();
                else if (ending == "cancel") token.Cancel();
                else source.FailRead();
                try { Check.That(!await reading.WaitAsync(TimeSpan.FromSeconds(5)) && ending == "eof", "stream ending result"); }
                catch (OperationCanceledException) when (ending == "cancel") { }
                catch (IOException) when (ending == "read-error") { }
                Check.That(!pools.Generations[0].Disposed, "ended read still conservatively holds response until disposal");
            }
            finally
            {
                foreach (var stream in wire.Streams) stream.EndChunks();
                if (reading != null) try { await reading; } catch (OperationCanceledException) when (ending == "cancel") { } catch (IOException) when (ending == "read-error") { }
                if (parser != null) await parser.DisposeAsync();
                if (body != null) await body.DisposeAsync(); first?.Dispose(); next?.Dispose();
            }
            Check.That(pools.Generations[0].Disposed && pools.Generations.All(g => g.Users == 0), "stream cleanup returns original generation once");
            Check.Pass("actual named " + name + " chunked SSE " + ending + ": first/second events before EOF, retry identity retained, old pool returned after joined consumer+response disposal");
        }
    }
    private static void DisposalFailureRetry()
    {
        var content = new DisposeFailureContent(); var lease = new CounterLease(); using var wrapper = Wrap(content, lease);
        bool failed = false;
        try
        {
            try { wrapper.Dispose(); } catch (IOException) { failed = true; }
            Check.That(failed && lease.Returns == 0, "failed content disposal must retain original lease");
        }
        finally { content.Fail = false; wrapper.Dispose(); }
        wrapper.Dispose();
        Check.That(content.Disposed && lease.Returns == 1, "retry content disposal returns exactly one lease");
        Check.Pass("stream content Dispose failure retains lease; original content retry returns once");
    }
    private sealed class DisposeFailureContent : HttpContent
    {
        internal bool Fail = true, Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { if (disposing && Fail) throw new IOException("Synthetic content dispose failure"); Disposed = true; base.Dispose(disposing); }
    }
    internal sealed class CounterLease : IDisposable
    {
        internal int Returns;
        private bool _disposed;
        public void Dispose() { if (!_disposed) { _disposed = true; Returns++; } }
    }
    internal sealed class ContinuousBody : Stream
    {
        private readonly byte[] _data = Encoding.UTF8.GetBytes("data: first\n\n");
        private int _position;
        internal int BytesRead;
        internal bool FailRead, Disposed, IgnoreCancellation;
        internal readonly Gate Finish = new();
        internal readonly TaskCompletionSource ReadWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _data.Length) { int count = Math.Min(buffer.Length, _data.Length - _position); _data.AsMemory(_position, count).CopyTo(buffer); _position += count; BytesRead += count; return count; }
            ReadWaiting.TrySetResult(); if (FailRead) throw new IOException("Synthetic body read failure");
            if (IgnoreCancellation) await Finish.Pause(); else await Finish.Pause().WaitAsync(cancellationToken); return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count)
        { if (_position >= _data.Length) return 0; int take = Math.Min(count, _data.Length - _position); _data.AsSpan(_position, take).CopyTo(buffer.AsSpan(offset)); _position += take; BytesRead += take; return take; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class ObservedWriteStream : MemoryStream
    {
        internal TaskCompletionSource Wrote { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Write(byte[] buffer, int offset, int count) { base.Write(buffer, offset, count); Wrote.TrySetResult(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { var task = base.WriteAsync(buffer, ct); Wrote.TrySetResult(); return task; }
    }
}
