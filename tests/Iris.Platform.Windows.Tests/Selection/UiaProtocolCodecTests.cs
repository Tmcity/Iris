using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Platform.Windows.Selection;
using Shouldly;
using Xunit;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class UiaProtocolCodecTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocolVersion\": 2, \"kind\": \"Ready\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\"}")]
    [InlineData("{\"kind\": \"Ready\"}")]
    public void DeserializeReady_WhenMalformedOrMissingFields_ReturnsNull(string json)
    {
        var frame = UiaProtocolCodec.DeserializeReady(json);
        frame.ShouldBeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Request\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Request\", \"requestId\": \"\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Request\", \"requestId\": \"r1\", \"hwnd\": 0, \"processId\": 10}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Request\", \"requestId\": \"r1\", \"hwnd\": 100, \"processId\": 0}")]
    public void DeserializeRequest_WhenMalformedOrMissingFields_ReturnsNull(string json)
    {
        var frame = UiaProtocolCodec.DeserializeRequest(json);
        frame.ShouldBeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocolVersion\": 1}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\", \"requestId\": \"r1\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\", \"requestId\": \"r1\", \"status\": \"UnknownStatus\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\", \"requestId\": \"r1\", \"status\": \"0\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\", \"requestId\": \"r1\", \"status\": \"999\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\", \"requestId\": \"r1\", \"status\": \"success\"}")]
    [InlineData("{\"protocolVersion\": 1, \"kind\": \"Reply\", \"requestId\": \"r1\", \"status\": 0}")]
    public void DeserializeReply_WhenMissingRequiredFieldsOrUnknownStatus_ReturnsNull(string json)
    {
        var frame = UiaProtocolCodec.DeserializeReply(json);
        frame.ShouldBeNull();
    }

    [Fact]
    public void DeserializeReply_WhenSuccessWithMissingOrWhitespaceText_ReturnsNull()
    {
        var jsonNoText = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Success"}""";
        UiaProtocolCodec.DeserializeReply(jsonNoText).ShouldBeNull();

        var jsonEmptyText = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Success","text":""}""";
        UiaProtocolCodec.DeserializeReply(jsonEmptyText).ShouldBeNull();

        var jsonWhitespaceText = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Success","text":"   \t\n"}""";
        UiaProtocolCodec.DeserializeReply(jsonWhitespaceText).ShouldBeNull();
    }

    [Fact]
    public void DeserializeReply_WhenSuccessWithTextExceedingLimit_ReturnsNull()
    {
        var hugeText = new string('x', 65537);
        var json = $$"""{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Success","text":"{{hugeText}}"}""";
        UiaProtocolCodec.DeserializeReply(json).ShouldBeNull();
    }

    [Fact]
    public void DeserializeReply_WhenSuccessWithErrorCode_ReturnsNull()
    {
        var json = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Success","text":"valid","errorCode":"ERROR"}""";
        UiaProtocolCodec.DeserializeReply(json).ShouldBeNull();
    }

    [Fact]
    public void DeserializeReply_WhenNonSuccessWithText_ReturnsNull()
    {
        var jsonEmptyWithText = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Empty","text":"some text"}""";
        UiaProtocolCodec.DeserializeReply(jsonEmptyWithText).ShouldBeNull();

        var jsonFailedWithText = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Failed","text":"some text","errorCode":"SOME_ERROR"}""";
        UiaProtocolCodec.DeserializeReply(jsonFailedWithText).ShouldBeNull();
    }

    [Theory]
    [InlineData("lowercase_error")]
    [InlineData("SPACES IN ERROR")]
    [InlineData("SPECIAL!@#$%")]
    [InlineData("VERY_LONG_ERROR_CODE_EXCEEDING_THIRTY_TWO_CHARS_BY_FAR")]
    public void DeserializeReply_WhenIllegalErrorCode_ReturnsNull(string badCode)
    {
        var json = $$"""{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Failed","errorCode":"{{badCode}}"}""";
        UiaProtocolCodec.DeserializeReply(json).ShouldBeNull();
    }

    [Fact]
    public void DeserializeReply_WhenValidReply_Succeeds()
    {
        var jsonSuccess = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Success","text":"valid selection"}""";
        var frame = UiaProtocolCodec.DeserializeReply(jsonSuccess);
        frame.ShouldNotBeNull();
        frame.Status.ShouldBe(UiaSelectionStatus.Success);
        frame.Text.ShouldBe("valid selection");

        var jsonEmpty = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Empty"}""";
        var emptyFrame = UiaProtocolCodec.DeserializeReply(jsonEmpty);
        emptyFrame.ShouldNotBeNull();
        emptyFrame.Status.ShouldBe(UiaSelectionStatus.Empty);

        var jsonFailed = """{"protocolVersion":1,"kind":"Reply","requestId":"r1","status":"Failed","errorCode":"COM_0x80040204"}""";
        var failedFrame = UiaProtocolCodec.DeserializeReply(jsonFailed);
        failedFrame.ShouldNotBeNull();
        failedFrame.Status.ShouldBe(UiaSelectionStatus.Failed);
        failedFrame.ErrorCode.ShouldBe("COM_0x80040204");
    }

    [Fact]
    public async Task FrameReader_WhenCrFlooding_WireBytesEnforceLimit()
    {
        // 100 bytes limit; send 105 CRs
        using var ms = new MemoryStream();
        var crs = new byte[105];
        Array.Fill(crs, (byte)'\r');
        ms.Write(crs);
        ms.Position = 0;

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await UiaProtocolFrameReader.ReadLineWithLimitAsync(ms, 100, CancellationToken.None));
    }

    [Fact]
    public async Task FrameReader_WhenInvalidUtf8Bytes_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        // 0xFF 0xFE are invalid UTF-8 sequence
        ms.Write([0xFF, 0xFE, (byte)'\n']);
        ms.Position = 0;

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await UiaProtocolFrameReader.ReadLineWithLimitAsync(ms, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task FrameReader_WhenCrLfDelimiter_TrimsTrailingCrCleanly()
    {
        using var ms = new MemoryStream();
        var lineBytes = Encoding.UTF8.GetBytes("hello world\r\n");
        ms.Write(lineBytes);
        ms.Position = 0;

        var line = await UiaProtocolFrameReader.ReadLineWithLimitAsync(ms, 1024, CancellationToken.None);
        line.ShouldBe("hello world");
    }

    [Fact]
    public async Task FrameReader_WhenUnexpectedIOException_ThrowsIOException()
    {
        var throwingStream = new ThrowingReadStream(new IOException("Unexpected disk or pipe fault"));
        await Should.ThrowAsync<IOException>(async () =>
            await UiaProtocolFrameReader.ReadLineWithLimitAsync(throwingStream, 1024, CancellationToken.None));
    }

    private sealed class ThrowingReadStream : Stream
    {
        private readonly Exception _toThrow;
        public ThrowingReadStream(Exception toThrow) => _toThrow = toThrow;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw _toThrow;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(_toThrow);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(_toThrow);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
