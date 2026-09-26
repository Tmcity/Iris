using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NetSse = System.Net.ServerSentEvents;

namespace Iris.Providers.Http;

/// <summary>
/// 包装 System.Net.ServerSentEvents.SseParser。不拥有 stream（不会释放它）。
/// 读取的字节总数超过 maxResponseBytes 时，抛 IrisException(InvalidResponse)。
/// 已知行为：流末尾不完整的事件（最后没有空行）会被丢弃；
/// 只有 event: 没有 data 的事件，其类型会延续到下一个事件（.NET 解析器与 WHATWG 规范的偏差，对 OpenAI 和 Anthropic 没有影响）。
/// </summary>
internal static class SseParser
{
    public const long DefaultMaxResponseBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 包装 System.Net.ServerSentEvents.SseParser。不拥有 stream（不会释放它）。
    /// 读取的字节总数超过 maxResponseBytes 时，抛 IrisException(InvalidResponse)。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        long maxResponseBytes = DefaultMaxResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var limitedStream = new LimitedReadStream(stream, maxResponseBytes);
        var parser = NetSse.SseParser.Create(limitedStream);

        await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseEvent(item.EventType, item.Data, item.EventId);
        }
    }
}
