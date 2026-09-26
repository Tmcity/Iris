using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Errors;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Http;

public sealed class SseParserTests
{
    private sealed class SingleByteReadStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public SingleByteReadStream(byte[] data)
        {
            _data = data;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length || count <= 0)
            {
                return 0;
            }
            buffer[offset] = _data[_position++];
            return 1;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _data.Length || buffer.IsEmpty)
            {
                return 0;
            }
            buffer[0] = _data[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }
    }

    private static async Task<List<SseEvent>> ReadAllAsync(Stream stream, CancellationToken ct, long maxBytes = SseParser.DefaultMaxResponseBytes)
    {
        var list = new List<SseEvent>();
        await foreach (var item in SseParser.ReadEventsAsync(stream, ct, maxBytes))
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task ReadEventsAsync_OpenAiFixture_ParsesFiveEventsWithMessageType()
    {
        const string sse =
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"，世界\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(5);
        events.ShouldAllBe(e => e.EventType == "message");
        events[0].Data.ShouldBe("{\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}");
        events[1].Data.ShouldBe("{\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\"},\"finish_reason\":null}]}");
        events[2].Data.ShouldBe("{\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"，世界\"},\"finish_reason\":null}]}");
        events[3].Data.ShouldBe("{\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}");
        events[4].Data.ShouldBe("[DONE]");
    }

    [Fact]
    public async Task ReadEventsAsync_AnthropicFixture_ParsesExpectedEventTypes()
    {
        const string sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":25,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(6);
        events[0].EventType.ShouldBe("message_start");
        events[1].EventType.ShouldBe("content_block_start");
        events[2].EventType.ShouldBe("content_block_delta");
        events[3].EventType.ShouldBe("content_block_stop");
        events[4].EventType.ShouldBe("message_delta");
        events[5].EventType.ShouldBe("message_stop");
    }

    [Fact]
    public async Task ReadEventsAsync_CrLfAndCrAndBom_ParsedCorrectly()
    {
        var bom = Encoding.UTF8.GetPreamble();
        var content = "data: item1\r\n\r\ndata: item2\r\r";
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var allBytes = new byte[bom.Length + contentBytes.Length];
        Buffer.BlockCopy(bom, 0, allBytes, 0, bom.Length);
        Buffer.BlockCopy(contentBytes, 0, allBytes, bom.Length, contentBytes.Length);

        using var stream = new MemoryStream(allBytes);
        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBe("item1");
        events[1].Data.ShouldBe("item2");
    }

    [Fact]
    public async Task ReadEventsAsync_CommentsAndMultilineDataAndColonSpacing()
    {
        const string sse =
            ": this is a comment\n" +
            "data: line1\n" +
            "data: line2\n\n" +
            "data:x\n\n" +
            "data:  x\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(3);
        events[0].Data.ShouldBe("line1\nline2");
        events[1].Data.ShouldBe("x");
        events[2].Data.ShouldBe(" x");
    }

    [Fact]
    public async Task ReadEventsAsync_SingleByteChunksWithSplitChineseCharacters_PreservesData()
    {
        const string sse = "data: 汉字测试，你好世界！\n\n";
        var bytes = Encoding.UTF8.GetBytes(sse);
        using var stream = new SingleByteReadStream(bytes);

        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBe("汉字测试，你好世界！");
    }

    [Fact]
    public async Task ReadEventsAsync_IncompleteEventAtEndOfStream_IsDropped()
    {
        const string sse = "data: first\n\ndata: second_incomplete";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBe("first");
    }

    [Fact]
    public async Task ReadEventsAsync_EventWithoutData_CarriesTypeToNextEvent()
    {
        const string sse = "event: ping\n\ndata: hello\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var events = await ReadAllAsync(stream, TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].EventType.ShouldBe("ping");
        events[0].Data.ShouldBe("hello");
    }

    [Fact]
    public async Task ReadEventsAsync_ExceedsMaxBytes_ThrowsIrisException()
    {
        var largeSse = new string('x', 2048);
        var sse = $"data: {largeSse}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in SseParser.ReadEventsAsync(stream, TestContext.Current.CancellationToken, maxResponseBytes: 1024))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.InvalidResponse);
        ex.Detail.ShouldNotBeNull();
        ex.Detail.ShouldContain("1024");
    }

    [Fact]
    public async Task ReadEventsAsync_CanceledDuringEnumeration_ThrowsOperationCanceledException()
    {
        const string sse = "data: item1\n\ndata: item2\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SseParser.ReadEventsAsync(stream, cts.Token))
            {
                await cts.CancelAsync();
            }
        });
    }
}
