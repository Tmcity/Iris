using System.Net;
using Iris.Core.Network;

namespace Iris.Providers.Http;

/// <summary>
/// A response, including retry intermediates, owns a separate handler reference. Stream acquisition never buffers.
/// The manager retains this exact owner if disposal fails; a disposed HttpResponseMessage is not the retry token.
/// </summary>
internal sealed class NetworkResponseContent : HttpContent
{
    private readonly NetworkTransportManager _manager;
    private readonly NetworkTransportManager.Entry _entry;
    private readonly HttpContent _inner;
    private readonly Lock _gate = new();
    private NetworkReadStream? _stream;
    private int _active;
    private bool _cleanupRequested;
    private bool _disposing;
    private bool _innerDisposed;
    private bool _completed;
    internal int ActiveOperations { get { lock (_gate) return _active; } }
    internal bool CleanupRequested { get { lock (_gate) return _cleanupRequested; } }

    internal NetworkResponseContent(NetworkTransportManager manager, NetworkTransportManager.Entry entry, HttpContent inner)
    {
        _manager = manager; _entry = entry; _inner = inner;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    private void Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_cleanupRequested || _completed, this);
            _active++;
        }
    }
    private void Leave() { lock (_gate) _active--; _manager.ContentChanged(); }
    private Exception ReadFailure(Exception error)
    {
        _manager.MarkFailed(_entry);
        return error switch
        {
            OperationCanceledException canceled => new OperationCanceledException("Network body read canceled.", null, canceled.CancellationToken),
            IOException io => new IOException("Network body IO failed.", io.HResult),
            HttpRequestException http => new HttpRequestException(http.HttpRequestError, "Network body read failed.", null, http.StatusCode),
            _ => NetworkProxyCodec.Unavailable()
        };
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? 0;
        return _inner.Headers.ContentLength.HasValue;
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            var raw = _inner.ReadAsStream(cancellationToken);
            lock (_gate) return _stream ??= new NetworkReadStream(this, raw);
        }
        catch (Exception ex) { throw ReadFailure(ex); }
        finally { Leave(); }
    }
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            var raw = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) return _stream ??= new NetworkReadStream(this, raw);
        }
        catch (Exception ex) { throw ReadFailure(ex); }
        finally { Leave(); }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { throw ReadFailure(ex); }
        finally { Leave(); }
    }
    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Enter();
        try { cancellationToken.ThrowIfCancellationRequested(); CreateContentReadStream(cancellationToken).CopyTo(stream); }
        catch (Exception ex) { throw ReadFailure(ex); }
        finally { Leave(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        lock (_gate)
        {
            if (_completed) return;
            _cleanupRequested = true;
            if (_active != 0 || _disposing) throw NetworkProxyCodec.Unavailable();
            _disposing = true;
        }
        try
        {
            // Dispose the actual borrowed stream first. This preserves a failed leaf even if HttpContent's
            // own base Dispose sets its flag before calling that stream's Dispose.
            _stream?.Dispose();
            if (!_innerDisposed) { _inner.Dispose(); _innerDisposed = true; }
            base.Dispose(true);
            lock (_gate) _completed = true;
            _manager.ContentCompleted(this, _entry);
        }
        catch
        {
            _manager.MarkFailed(_entry);
            throw NetworkProxyCodec.Unavailable();
        }
        finally { lock (_gate) _disposing = false; _manager.ContentChanged(); }
    }

    private sealed class NetworkReadStream(NetworkResponseContent owner, Stream inner) : Stream
    {
        private readonly Lock _disposeGate = new();
        private bool _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            owner.Enter();
            try { return inner.Read(buffer); }
            catch (Exception ex) { throw owner.ReadFailure(ex); }
            finally { owner.Leave(); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Enter();
            try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { throw owner.ReadFailure(ex); }
            finally { owner.Leave(); }
        }
        public override void CopyTo(Stream destination, int bufferSize)
        {
            owner.Enter();
            try { inner.CopyTo(destination, bufferSize); }
            catch (Exception ex) { throw owner.ReadFailure(ex); }
            finally { owner.Leave(); }
        }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            owner.Enter();
            try { await inner.CopyToAsync(destination, bufferSize, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { throw owner.ReadFailure(ex); }
            finally { owner.Leave(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_disposeGate)
                {
                    if (!_disposed)
                    {
                        if (owner.ActiveOperations != 0) throw NetworkProxyCodec.Unavailable();
                        try { inner.Dispose(); _disposed = true; }
                        catch { owner._manager.MarkFailed(owner._entry); throw NetworkProxyCodec.Unavailable(); }
                    }
                }
            }
            base.Dispose(disposing);
        }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
