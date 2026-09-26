using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Providers.Http;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public sealed class LimitedReadStreamTests
{
    private sealed class TrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        public TrackingStream(byte[] data) : base(data)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
        }
    }

    [Fact]
    public void Read_ByteArray_CountsTotalBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        var buffer = new byte[3];
        var read = stream.Read(buffer, 0, 3);

        read.ShouldBe(3);
        stream.TotalBytesRead.ShouldBe(3);
    }

    [Fact]
    public void Read_Span_CountsTotalBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        Span<byte> buffer = stackalloc byte[4];
        var read = stream.Read(buffer);

        read.ShouldBe(4);
        stream.TotalBytesRead.ShouldBe(4);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA1835:Prefer ReadAsync with Memory", Justification = "Testing byte[] ReadAsync overload directly")]
    public async Task ReadAsync_ByteArray_CountsTotalBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        var buffer = new byte[3];
        var read = await stream.ReadAsync(buffer, 0, 3, TestContext.Current.CancellationToken);

        read.ShouldBe(3);
        stream.TotalBytesRead.ShouldBe(3);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2022:Avoid inexact read with Stream.Read", Justification = "Testing ReadAsync overload directly")]
    public async Task ReadAsync_Memory_CountsTotalBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        var buffer = new byte[5];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 5), TestContext.Current.CancellationToken);

        read.ShouldBe(5);
        stream.TotalBytesRead.ShouldBe(5);
    }

    [Fact]
    public void Read_ExactlyAtLimit_DoesNotThrow()
    {
        var data = new byte[10];
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        var buffer = new byte[10];
        var read = stream.Read(buffer, 0, 10);

        read.ShouldBe(10);
        stream.TotalBytesRead.ShouldBe(10);
    }

    [Fact]
    public void Read_ExceedsLimitByOneByte_ThrowsIrisException()
    {
        var data = new byte[11];
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        var buffer = new byte[11];
        var ex = Should.Throw<IrisException>(() => stream.Read(buffer, 0, 11));

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("10");
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2022:Avoid inexact read with Stream.Read", Justification = "Testing ReadAsync overload directly")]
    public async Task ReadAsync_ExceedsLimitByOneByte_ThrowsIrisException()
    {
        var data = new byte[11];
        using var inner = new MemoryStream(data);
        using var stream = new LimitedReadStream(inner, 10);

        var buffer = new byte[11];
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            _ = await stream.ReadAsync(buffer.AsMemory(0, 11), TestContext.Current.CancellationToken);
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("10");
    }

    [Fact]
    public void Dispose_DoesNotDisposeInnerStream()
    {
        var data = new byte[] { 1, 2, 3 };
        var inner = new TrackingStream(data);
        var stream = new LimitedReadStream(inner, 10);

        stream.Dispose();

        inner.IsDisposed.ShouldBeFalse();
    }

    [Fact]
    public void PropertiesAndUnsupportedMethods_MatchSpecification()
    {
        using var inner = new MemoryStream([1, 2, 3]);
        using var stream = new LimitedReadStream(inner, 5);

        stream.CanRead.ShouldBeTrue();
        stream.CanSeek.ShouldBeFalse();
        stream.CanWrite.ShouldBeFalse();

        Should.Throw<NotSupportedException>(() => _ = stream.Length);
        Should.Throw<NotSupportedException>(() => _ = stream.Position);
        Should.Throw<NotSupportedException>(() => stream.Position = 0);
        Should.Throw<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => stream.SetLength(0));
        Should.Throw<NotSupportedException>(() => stream.Write([1], 0, 1));
        Should.Throw<NotSupportedException>(() => stream.Flush());
    }
}
