using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Iris.Providers.Translation.Anthropic;

/// <summary>
/// 包装底层网络流，并在读取到完整 SSE 事件边界时触发续期回调。负责底层流与 HttpContent 的生命周期释放。
/// </summary>
internal sealed class SseEventBoundaryStream : Stream
{
    private readonly Stream _innerStream;
    private readonly HttpContent _originalContent;
    private readonly SseEventBoundaryTracker _tracker;

    public SseEventBoundaryStream(Stream innerStream, HttpContent originalContent, Action onEventBoundary)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _originalContent = originalContent ?? throw new ArgumentNullException(nameof(originalContent));
        ArgumentNullException.ThrowIfNull(onEventBoundary);
        _tracker = new SseEventBoundaryTracker(onEventBoundary);
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _innerStream.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _innerStream.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _innerStream.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        if (bytesRead > 0)
        {
            _tracker.ProcessBytes(buffer.AsSpan(offset, bytesRead));
        }
        return bytesRead;
    }

    public override int Read(Span<byte> buffer)
    {
        var bytesRead = _innerStream.Read(buffer);
        if (bytesRead > 0)
        {
            _tracker.ProcessBytes(buffer[..bytesRead]);
        }
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        if (bytesRead > 0)
        {
            _tracker.ProcessBytes(buffer.AsSpan(offset, bytesRead));
        }
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (bytesRead > 0)
        {
            _tracker.ProcessBytes(buffer.Span[..bytesRead]);
        }
        return bytesRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
            _originalContent.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync().ConfigureAwait(false);
        _originalContent.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
