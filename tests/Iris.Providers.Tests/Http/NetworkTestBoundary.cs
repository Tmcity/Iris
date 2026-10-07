using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Iris.Core.Network;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Providers.Tests.Http;

/// <summary>Existing business pipeline tests use an explicitly direct, managed snapshot boundary.</summary>
internal sealed class DirectSnapshotSource : INetworkProxySnapshotSource
{
    public Task<NetworkProxySnapshot> CaptureCurrentAsync(Uri destination, CancellationToken cancellationToken = default) =>
        Task.FromResult(new NetworkProxySnapshot(Guid.Empty, destination, ProxyMode.None, null, null, ProxyRouteKind.DirectByMode));
    public Task<NetworkProxySnapshot> CaptureDraftAsync(ProxyDraft draft, Uri destination, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    internal static void Register(IServiceCollection services) => services.AddSingleton<INetworkProxySnapshotSource, DirectSnapshotSource>();
}

// Adapted only the scripted byte-stream boundary from T-036A. All routing, ownership and retries are production code.
internal sealed class NetworkWire
{
    internal readonly ConcurrentQueue<ScriptedStream> Streams = new();
    internal readonly ConcurrentQueue<string> Endpoints = new();
    internal readonly ConcurrentQueue<string> Auth = new();
    internal Func<string, int, string>? Respond;
    internal bool RequireAuth = true;
    private int _requests;
    internal ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Endpoints.Enqueue(context.DnsEndPoint.Host + ":" + context.DnsEndPoint.Port);
        var stream = new ScriptedStream(headers =>
        {
            var auth = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
            if (RequireAuth && auth == null) return "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"synthetic\"\r\nContent-Length: 0\r\n\r\n";
            if (auth != null) Auth.Enqueue(Encoding.UTF8.GetString(Convert.FromBase64String(auth[(auth.LastIndexOf(' ') + 1)..])));
            int n = Interlocked.Increment(ref _requests);
            return Respond?.Invoke(headers, n) ?? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
        });
        Streams.Enqueue(stream); return ValueTask.FromResult<Stream>(stream);
    }
    internal void ReleaseAll() { foreach (var stream in Streams) stream.EndChunks(); }

    internal sealed class ScriptedStream(Func<string, string> respond) : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly StringBuilder _request = new();
        private byte[]? _current;
        private int _offset;
        private Exception? _readFailure;
        internal bool Disposed;
        internal TaskCompletionSource ReadWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static string Chunk(string data) => Encoding.UTF8.GetByteCount(data).ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" + data + "\r\n";
        internal void EmitChunk(string data) => _responses.Writer.TryWrite(Encoding.UTF8.GetBytes(Chunk(data)));
        internal void EndChunks() => _responses.Writer.TryWrite("0\r\n\r\n"u8.ToArray());
        internal void FailRead() { _readFailure = new IOException("Synthetic wire read failure"); _responses.Writer.TryComplete(_readFailure); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current == null || _offset == _current.Length)
            {
                try
                {
                    var read = _responses.Reader.ReadAsync(cancellationToken);
                    if (!read.IsCompleted) ReadWaiting.TrySetResult();
                    _current = await read; _offset = 0;
                }
                catch (ChannelClosedException) { if (_readFailure != null) throw _readFailure; return 0; }
            }
            int count = Math.Min(buffer.Length, _current.Length - _offset); _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private void WriteCore(ReadOnlySpan<byte> buffer)
        {
            _request.Append(Encoding.ASCII.GetString(buffer));
            while (_request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end && end >= 0)
            {
                string headers = _request.ToString(0, end + 4);
                string? lengthHeader = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                int bodyLength = lengthHeader == null ? 0 : int.Parse(lengthHeader[(lengthHeader.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                if (_request.Length < end + 4 + bodyLength) return;
                string request = _request.ToString(0, end + 4 + bodyLength); _request.Remove(0, end + 4 + bodyLength);
                _responses.Writer.TryWrite(Encoding.UTF8.GetBytes(respond(request)));
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteCore(buffer.AsSpan(offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { WriteCore(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { WriteCore(buffer.AsSpan(offset, count)); return Task.CompletedTask; }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; _responses.Writer.TryComplete(); } base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
