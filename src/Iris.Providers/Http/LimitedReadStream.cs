using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;

namespace Iris.Providers.Http;

/// <summary>
/// 只读包装流，累计已读字节数。
/// 读取的字节总数超过 maxBytes 时抛出 IrisException(InvalidResponse)。
/// 不拥有/释放内部流。
/// </summary>
internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _maxBytes;
    private long _totalBytesRead;

    public LimitedReadStream(Stream innerStream, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _innerStream = innerStream;
        _maxBytes = maxBytes;
    }

    public long TotalBytesRead => _totalBytesRead;

    public long MaxBytes => _maxBytes;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override bool CanTimeout => _innerStream.CanTimeout;

    public override int ReadTimeout
    {
        get => _innerStream.ReadTimeout;
        set => _innerStream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _innerStream.WriteTimeout;
        set => _innerStream.WriteTimeout = value;
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _innerStream.Read(buffer, offset, count);
        TrackAndCheckLimit(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _innerStream.Read(buffer);
        TrackAndCheckLimit(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        TrackAndCheckLimit(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        TrackAndCheckLimit(read);
        return read;
    }

    private void TrackAndCheckLimit(int bytesRead)
    {
        if (bytesRead <= 0)
        {
            return;
        }

        if (_totalBytesRead + bytesRead > _maxBytes)
        {
            throw new IrisException(
                ErrorCode.InvalidResponse,
                ErrorMessages.GetDefault(ErrorCode.InvalidResponse),
                detail: $"响应超过 {_maxBytes} 字节");
        }

        _totalBytesRead += bytesRead;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // 不释放内部流
    }
}
