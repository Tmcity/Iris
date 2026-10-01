using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation.Anthropic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NetSse = System.Net.ServerSentEvents;
using NSubstitute;
using RichardSzalay.MockHttp;
using Shouldly;
using Xunit;

namespace Iris.Providers.Tests.Translation.Anthropic;

[Collection("AnthropicTests")]
public sealed class AnthropicReworkR1Tests
{
    private static AnthropicTranslationProvider CreateProvider(
        HttpMessageHandler handler,
        TranslationProviderProfile? profile = null,
        ProviderCredentials? credentials = null,
        FakeTimeProvider? timeProvider = null,
        int timeoutSeconds = 30)
    {
        var prof = profile ?? new TranslationProviderProfile
        {
            Id = "p-rework-test",
            DisplayName = "Test Anthropic Rework",
            Kind = TranslationProviderKind.Anthropic,
            BaseUrl = null,
            Model = "claude-sonnet-5",
            TimeoutSeconds = timeoutSeconds,
        };

        var creds = credentials ?? new ProviderCredentials("sk-ant-test-key-12345678");

        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(HttpClientNames.Llm).Returns(httpClient);

        var promptTemplates = Substitute.For<IPromptTemplateProvider>();
        promptTemplates.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);

        var promptRenderer = Substitute.For<IPromptRenderer>();
        promptRenderer.Render(Arg.Any<PromptTemplate>(), Arg.Any<ProviderTranslateRequest>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<ProviderTranslateRequest>();
                return new RenderedPrompt(System: "You are a translator.", User: $"Translate: {req.Text}");
            });

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());

        var tp = timeProvider ?? new FakeTimeProvider();
        var lf = new LoggerFactory();
        var logger = lf.CreateLogger<AnthropicTranslationProvider>();

        return new AnthropicTranslationProvider(
            prof,
            creds,
            httpClientFactory,
            promptTemplates,
            promptRenderer,
            settings,
            tp,
            logger);
    }

    #region R1-1: 移除额外 HttpClient 与默认超时

    [Fact]
    public async Task R1_1_ProfileTimeout300s_DoesNotTimeOutAt100s_TimesOutAt300s()
    {
        var timeProvider = new FakeTimeProvider();
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 300);

        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hello", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();

        // 推进到 150 秒（超过默认 HttpClient 的 100 秒超时）
        timeProvider.Advance(TimeSpan.FromSeconds(150));

        // 任务不应被 100s 超时提前取消
        readTask.IsCompleted.ShouldBeFalse();

        // 再次推进 150 秒（达到 300 秒阈值）
        timeProvider.Advance(TimeSpan.FromSeconds(150));

        var ex = await Should.ThrowAsync<IrisException>(async () => await readTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.Timeout);
    }

    [Fact]
    public async Task R1_1_UserCancellation_HasPriorityOverTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 300);
        using var userCts = new CancellationTokenSource();

        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hello", "en", "zh-CN"), userCts.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();

        // 用户主动取消
        userCts.Cancel();

        // 必须原样抛出 OperationCanceledException，不得转化为 IrisException(Timeout)
        await Should.ThrowAsync<OperationCanceledException>(async () => await readTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    #endregion

    #region R1-2: 延迟执行

    [Fact]
    public async Task R1_2_Anthropic_TranslateStreamAsync_DeferredExecutionThreePhases()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "p-unconf",
            DisplayName = "Unconfigured",
            Kind = TranslationProviderKind.Anthropic,
            Model = "", // 未配置
        };
        var mockHttp = new MockHttpMessageHandler();
        var provider = CreateProvider(mockHttp, profile: profile);

        // 阶段 0：同步参数校验立即抛 ArgumentNullException
        Should.Throw<ArgumentNullException>(() => provider.TranslateStreamAsync(null!));

        // 阶段 1：获取序列不抛出异常、不发 HTTP 请求
        var enumerable = provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken);
        enumerable.ShouldNotBeNull();
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);

        // 阶段 2：创建枚举器不抛出异常、不发 HTTP 请求
        var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        enumerator.ShouldNotBeNull();
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);

        // 阶段 3：首次 MoveNextAsync 抛出 NotConfigured 并携带正确 ProviderId
        var ex = await Should.ThrowAsync<IrisException>(async () => await enumerator.MoveNextAsync());
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
        ex.ProviderId.ShouldBe("p-unconf");
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);
    }

    [Fact]
    public async Task R1_2_Anthropic_ListModelsAsync_DeferredExecution()
    {
        var profile = new TranslationProviderProfile
        {
            Id = "p-unconf-models",
            DisplayName = "Unconfigured Models",
            Kind = TranslationProviderKind.Anthropic,
            Model = "", // 未配置
        };
        var mockHttp = new MockHttpMessageHandler();
        var provider = CreateProvider(mockHttp, profile: profile, credentials: ProviderCredentials.None);

        // 调用并等待 Task 时抛出 NotConfigured
        var ex = await Should.ThrowAsync<IrisException>(() => provider.ListModelsAsync(TestContext.Current.CancellationToken));
        ex.Code.ShouldBe(ErrorCode.NotConfigured);
        ex.ProviderId.ShouldBe("p-unconf-models");
        mockHttp.GetMatchCount(mockHttp.When("*")).ShouldBe(0);
    }

    #endregion

    #region R1-3: 响应包装失败及各类生命周期下的资源释放

    [Fact]
    public async Task R1_3_StreamAcquisitionFailure_DisposesResponseAndContent()
    {
        var failingContent = new DisposableTrackingContent(throwOnReadStream: true);
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = failingContent,
            });

        var provider = CreateProvider(mockHttp);
        await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        failingContent.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task R1_3_StreamAcquisitionCancellation_DisposesResponseAndContent()
    {
        var cancellingContent = new DisposableTrackingContent(cancelOnReadStream: true);
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = cancellingContent,
            });

        var provider = CreateProvider(mockHttp);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        cancellingContent.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task R1_3_EarlyBreak_DisposesResponseStreamAndContent()
    {
        var trackingStream = new DisposableTrackingStream(new MemoryStream(Encoding.UTF8.GetBytes(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" World\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n")));

        var trackingContent = new StreamContent(trackingStream)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
        };

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = trackingContent,
            });

        var provider = CreateProvider(mockHttp);

        // 仅消费第一个 chunk 便 break 退出
        await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                break;
            }
        }

        trackingStream.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task R1_3_NormalCompletion_DisposesResponseStreamAndContent()
    {
        var trackingStream = new DisposableTrackingStream(new MemoryStream(Encoding.UTF8.GetBytes(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n")));

        var trackingContent = new StreamContent(trackingStream)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
        };

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = trackingContent,
            });

        var provider = CreateProvider(mockHttp);
        await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
        {
        }

        trackingStream.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task R1_3_StreamReadCancellation_DisposesResponseStreamAndContent()
    {
        var stream = new ControllableStream();
        var trackingStream = new DisposableTrackingStream(stream);
        var trackingContent = new DisposableTrackingContent(trackingStream)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
        };

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = trackingContent,
            });

        var provider = CreateProvider(mockHttp);
        using var userCts = new CancellationTokenSource();

        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), userCts.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        await stream.WaitForReadAsync();
        stream.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");

        await stream.WaitForReadAsync();
        userCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await readTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // 验证取消时底层流与 HttpContent 均被实际释放
        trackingStream.IsDisposed.ShouldBeTrue();
        trackingContent.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public void R1_3_ProviderDispose_IsIdempotent()
    {
        var transportHandler = new DisposableTrackingHandler();
        var provider = CreateProvider(transportHandler);

        transportHandler.IsDisposed.ShouldBeFalse();

        // 第一次 Dispose 释放底层客户端与传输 Handler
        Should.NotThrow(() => provider.Dispose());
        transportHandler.IsDisposed.ShouldBeTrue();

        // 第二次 Dispose 幂等安全
        Should.NotThrow(() => provider.Dispose());
        transportHandler.IsDisposed.ShouldBeTrue();
    }

    #endregion

    #region R1-4: SSE 边界解析器单元测试与并发隔离

    [Fact]
    public void R1_4_SseEventBoundaryTracker_LineEndings_LF_CRLF_CR()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);

        // 1. LF: \n\n
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("data: one\n\n"));
        count.ShouldBe(1);

        // 2. CRLF: \r\n\r\n
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("data: two\r\n\r\n"));
        count.ShouldBe(2);

        // 3. CR: \r\r
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("data: three\r\r"));
        count.ShouldBe(3);
    }

    [Fact]
    public void R1_4_SseEventBoundaryTracker_CompletePing_ResetsTimeout()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);

        // ping with data
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("event: ping\ndata: {\"type\":\"ping\"}\n\n"));
        count.ShouldBe(1);

        // ping without data (does not increment)
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("event: ping\n\n"));
        count.ShouldBe(1);

        // ping with spaces around without data (does not increment)
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("event:  ping  \r\n\r\n"));
        count.ShouldBe(1);

        // ping with empty data value (has data: so increments)
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("event: ping\ndata:\n\n"));
        count.ShouldBe(2);
    }

    [Fact]
    public void R1_4_SseEventBoundaryTracker_FragmentedBytesWithoutEmptyLine_DoNotResetTimeout()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);

        tracker.ProcessBytes(Encoding.UTF8.GetBytes("data: hello world"));
        count.ShouldBe(0);

        tracker.ProcessBytes(Encoding.UTF8.GetBytes("   more content without newline"));
        count.ShouldBe(0);

        tracker.ProcessBytes(Encoding.UTF8.GetBytes("\n")); // end of data line, but not empty line
        count.ShouldBe(0);

        tracker.ProcessBytes(Encoding.UTF8.GetBytes("   spaces on new line"));
        count.ShouldBe(0);

        // Now empty line arrives
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("\n\n"));
        count.ShouldBe(1);
    }

    [Fact]
    public void R1_4_SseEventBoundaryTracker_NonDataLines_DoNotResetTimeout()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);

        // Comment line followed by empty line
        tracker.ProcessBytes(Encoding.UTF8.GetBytes(":keepalive comment\n\n"));
        count.ShouldBe(0);

        // Only blank lines
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("\n\n\n\n"));
        count.ShouldBe(0);
    }

    [Fact]
    public void R1_4_SseEventBoundaryTracker_MultiByteUtf8SplitAcrossChunks()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);

        var fullBytes = Encoding.UTF8.GetBytes("data: {\"text\":\"你好世界\"}\n\n");

        // Split in the middle of UTF-8 multibyte sequence
        var part1 = fullBytes[..17];
        var part2 = fullBytes[17..];

        tracker.ProcessBytes(part1);
        count.ShouldBe(0);

        tracker.ProcessBytes(part2);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task R1_4_Concurrent_AB_Isolation_PingInADoesNotKeepBAlive()
    {
        var timeProvider = new FakeTimeProvider();
        var streamA = new ControllableStream();
        var streamB = new ControllableStream();

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .With(req => req.Content?.ReadAsStringAsync().GetAwaiter().GetResult().Contains("text A") == true)
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(streamA)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .With(req => req.Content?.ReadAsStringAsync().GetAwaiter().GetResult().Contains("text B") == true)
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(streamB)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);

        // 启动请求 A 与请求 B
        var taskA = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("text A", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        var taskB = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("text B", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        await streamA.WaitForReadAsync();
        await streamB.WaitForReadAsync();

        // 两个请求均接收 message_start
        streamA.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_a\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        streamB.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_b\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");

        await streamA.WaitForReadAsync();
        await streamB.WaitForReadAsync();

        // t=6s: A 收到完整 ping，B 静默
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        streamA.Push("event: ping\ndata: {\"type\":\"ping\"}\n\n");
        await streamA.WaitForReadAsync();

        // t=12s (6s later): A 再次收到完整 ping，B 仍然静默
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        // 此时 B 已经静默 12 秒 (>10s)，应已超时
        var exB = await Should.ThrowAsync<IrisException>(async () => await taskB.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        exB.Code.ShouldBe(ErrorCode.Timeout);

        // A 不受影响，继续发送完成事件
        await streamA.WaitForReadAsync();
        streamA.Push("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        streamA.Push("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Result A\"}}\n\n");
        streamA.Push("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n");
        streamA.Push("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        streamA.Complete();

        await taskA.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        taskA.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task R1_4_Concurrent_AB_Isolation_CancellingADoesNotAbortB()
    {
        var streamA = new ControllableStream();
        var streamB = new ControllableStream();

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .With(req => req.Content?.ReadAsStringAsync().GetAwaiter().GetResult().Contains("text A") == true)
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(streamA)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .With(req => req.Content?.ReadAsStringAsync().GetAwaiter().GetResult().Contains("text B") == true)
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(streamB)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        var provider = CreateProvider(mockHttp);
        using var ctsA = new CancellationTokenSource();

        var taskA = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("text A", "en", "zh-CN"), ctsA.Token))
            {
            }
        }, TestContext.Current.CancellationToken);

        var taskB = Task.Run(async () =>
        {
            var results = new List<string>();
            await foreach (var chunk in provider.TranslateStreamAsync(new ProviderTranslateRequest("text B", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    results.Add(chunk.Delta);
                }
            }
            return results;
        }, TestContext.Current.CancellationToken);

        await streamA.WaitForReadAsync();
        await streamB.WaitForReadAsync();

        // 取消 A
        ctsA.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await taskA.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // B 继续正常输出并完成
        streamB.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_b\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
        streamB.Push("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        streamB.Push("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello B\"}}\n\n");
        streamB.Push("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n");
        streamB.Push("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        streamB.Complete();

        var resB = await taskB.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        resB.ShouldContain("Hello B");
    }

    #endregion

    #region R1-5: SSE 错误按结构化类型分类

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"request_too_large\",\"message\":\"Too big\"}}", 413, ErrorCode.InputTooLong, false)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Rate limit\"}}", 429, ErrorCode.RateLimited, true)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", 529, ErrorCode.ProviderUnavailable, true)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"some_future_type\",\"message\":\"request_too_large is in the message but type is not\"}}", 500, ErrorCode.ProviderUnavailable, true)]
    [InlineData("{\"type\":\"error\",\"error\":{\"message\":\"missing type\"}}", 500, ErrorCode.ProviderUnavailable, true)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":12345,\"message\":\"type is int\"}}", 500, ErrorCode.ProviderUnavailable, true)]
    [InlineData("{\"type\":\"request_too_large\"}", 500, ErrorCode.ProviderUnavailable, true)]
    [InlineData("not a json error", 500, ErrorCode.ProviderUnavailable, true)]
    public async Task R1_5_StructuredErrorTypeMapping_Tests(string errorJson, int expectedStatus, ErrorCode expectedCode, bool expectedRetryable)
    {
        var mockHttp = new MockHttpMessageHandler();
        var ssePayload = $"event: error\ndata: {errorJson}\n\n";
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", ssePayload);

        var provider = CreateProvider(mockHttp);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(expectedCode);
        ex.HttpStatus.ShouldBe(expectedStatus);
        ex.Retryable.ShouldBe(expectedRetryable);
        ex.InnerException.ShouldBeNull(); // 安全要求：禁止透传
    }

    [Fact]
    public void R1_5_IsRequestTooLarge_UnitTests()
    {
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":{\"type\":\"request_too_large\"}}").ShouldBeTrue();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"error\":{\"type\":\"request_too_large\"}}").ShouldBeTrue();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"request_too_large\"}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":{\"type\":\"REQUEST_TOO_LARGE\"}}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":{\"type\":\"other\",\"message\":\"request_too_large\"}}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":{}}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":null}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":\"not an object\"}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":{\"type\":null}}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"error\",\"error\":{\"type\":12345}}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"error\":null}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("{\"error\":\"not an object\"}").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("").ShouldBeFalse();
        AnthropicTranslationProvider.IsRequestTooLarge("invalid json").ShouldBeFalse();
    }

    #endregion

    #region R1-6: 模型列表序数去重与大小写排序

    [Fact]
    public async Task R1_6_ListModels_DeduplicatesOrdinal_SortsOrdinalIgnoreCase()
    {
        var mockHttp = new MockHttpMessageHandler();
        var jsonPage1 = """
        {
            "data": [
                {"id": "Model-X"},
                {"id": "model-x"},
                {"id": "Model-X"},
                {"id": "Alpha"}
            ],
            "has_more": true,
            "first_id": "Model-X",
            "last_id": "Alpha"
        }
        """;
        var jsonPage2 = """
        {
            "data": [
                {"id": "alpha"},
                {"id": "Alpha"},
                {"id": "Beta"}
            ],
            "has_more": false,
            "first_id": "alpha",
            "last_id": "Beta"
        }
        """;

        mockHttp.When(HttpMethod.Get, "https://api.anthropic.com/v1/models*")
            .With(req => req.RequestUri != null && req.RequestUri.Query.Contains("after_id"))
            .Respond("application/json", jsonPage2);

        mockHttp.When(HttpMethod.Get, "https://api.anthropic.com/v1/models*")
            .Respond("application/json", jsonPage1);

        var provider = CreateProvider(mockHttp);
        var models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);

        // 验证去重：精确相同的 Model-X 与 Alpha 跨页去重只保留一个；仅大小写不同的保留
        models.ShouldContain("Model-X");
        models.ShouldContain("model-x");
        models.ShouldContain("Alpha");
        models.ShouldContain("alpha");
        models.ShouldContain("Beta");

        models.Count(m => m == "Model-X").ShouldBe(1);
        models.Count(m => m == "Alpha").ShouldBe(1);

        // 验证排序：按 OrdinalIgnoreCase 排序
        var sorted = models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        models.ShouldBe(sorted);
    }

    #endregion

    #region R2: SSE 边界与错误类型判定收紧

    [Fact]
    public void R2_1_SseBoundary_Differential_PingWithoutData_CountZero()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("event: ping\n\n"));
        count.ShouldBe(0);
    }

    [Fact]
    public void R2_1_SseBoundary_Differential_DataIgnored_CountZero()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("data ignored\n\n"));
        count.ShouldBe(0);
    }

    [Fact]
    public void R2_1_SseBoundary_Differential_BomBeforeData_CountOne()
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);
        tracker.ProcessBytes(Encoding.UTF8.GetBytes("\uFEFFdata: hello\n\n"));
        count.ShouldBe(1);
    }

    [Fact]
    public void R2_2_IsRequestTooLarge_RootTypeWithoutErrorObject_ReturnsFalse()
    {
        AnthropicTranslationProvider.IsRequestTooLarge("{\"type\":\"request_too_large\"}").ShouldBeFalse();
    }

    [Fact]
    public async Task R2_2_ProviderError_MissingErrorType_MapsTo500ProviderUnavailable()
    {
        var mockHttp = new MockHttpMessageHandler();
        var ssePayload = "event: error\ndata: {\"type\":\"request_too_large\"}\n\n";
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond("text/event-stream", ssePayload);

        var provider = CreateProvider(mockHttp);
        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        ex.HttpStatus.ShouldBe(500);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task R2_1_ProviderStream_PingWithoutData_DoesNotResetTimeout_TimesOut_Sonnet()
    {
        var timeProvider = new FakeTimeProvider();
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        var provider = CreateProvider(mockHttp, timeProvider: timeProvider, timeoutSeconds: 10);
        var task = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            await stream.WaitForReadAsync();
            stream.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
            await stream.WaitForReadAsync();

            // t=6s: 发送无 data 的 ping，按规范不能续期
            timeProvider.Advance(TimeSpan.FromSeconds(6));
            stream.Push("event: ping\n\n");
            await stream.WaitForReadAsync();

            // t=12s (再过 6s，总静默时间 12s > 10s)：应该超时
            timeProvider.Advance(TimeSpan.FromSeconds(6));

            var ex = await Should.ThrowAsync<IrisException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            ex.Code.ShouldBe(ErrorCode.Timeout);
        }
        finally
        {
            stream.Complete();
        }
    }

    [Fact]
    public async Task R2_1_ProviderStream_PingWithoutData_DoesNotResetTimeout_TimesOut_Opus()
    {
        var timeProvider = new FakeTimeProvider();
        var stream = new ControllableStream();
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
                }
            });

        var profile = new TranslationProviderProfile
        {
            Id = "p-rework-opus",
            DisplayName = "Test Opus",
            Kind = TranslationProviderKind.Anthropic,
            Model = "claude-opus-5-20250514",
            TimeoutSeconds = 10,
            AnthropicOptions = new AnthropicOptions { RefusalFallback = true },
        };

        var provider = CreateProvider(mockHttp, profile: profile, timeProvider: timeProvider, timeoutSeconds: 10);
        var task = Task.Run(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(new ProviderTranslateRequest("hi", "en", "zh-CN"), TestContext.Current.CancellationToken))
            {
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            await stream.WaitForReadAsync();
            stream.Push("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n");
            await stream.WaitForReadAsync();

            // t=6s: 发送无 data 的 ping，按规范不能续期
            timeProvider.Advance(TimeSpan.FromSeconds(6));
            stream.Push("event: ping\n\n");
            await stream.WaitForReadAsync();

            // t=12s (再过 6s，总静默时间 12s > 10s)：应该超时
            timeProvider.Advance(TimeSpan.FromSeconds(6));

            var ex = await Should.ThrowAsync<IrisException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            ex.Code.ShouldBe(ErrorCode.Timeout);
        }
        finally
        {
            stream.Complete();
        }
    }

    [Theory]
    [InlineData("event: ping\ndata: {\"type\":\"ping\"}\n\n", 1)]
    [InlineData("event: ping\n\n", 0)]
    [InlineData("data ignored\n\n", 0)]
    [InlineData("data:\n\n", 1)]
    [InlineData("\uFEFFdata: hello\n\n", 1)]
    [InlineData("data: hello", 0)]
    [InlineData(":comment\n\n", 0)]
    [InlineData("data: first\ndata: second\n\n", 1)]
    [InlineData("data\n\n", 1)]
    [InlineData("data: one\r\r", 1)]
    [InlineData("data: one\r\n\r\n", 1)]
    [InlineData("\uFEFFdata: {\"text\":\"你好世界\"}\n\n", 1)]
    public async Task R2_1_SseParser_Differential_ComparisonWithDotNetSseParser(string sseInput, int expectedEvents)
    {
        var bytes = Encoding.UTF8.GetBytes(sseInput);

        // 1. .NET SseParser 产出事件数验证
        var dotNetCount = await CountEventsUsingDotNetSseParserAsync(bytes);
        dotNetCount.ShouldBe(expectedEvents);

        // 2. 整块输入
        CountEventsUsingTracker(bytes, bytes.Length).ShouldBe(expectedEvents);

        // 3. 逐字节输入（测试跨字节状态机与跨 chunk UTF-8/BOM）
        CountEventsUsingTracker(bytes, 1).ShouldBe(expectedEvents);

        // 4. 双字节切分
        CountEventsUsingTracker(bytes, 2).ShouldBe(expectedEvents);

        // 5. 3 字节切分（对应 BOM 边界）
        CountEventsUsingTracker(bytes, 3).ShouldBe(expectedEvents);

        // 6. 7 字节切分
        CountEventsUsingTracker(bytes, 7).ShouldBe(expectedEvents);
    }

    private static async Task<int> CountEventsUsingDotNetSseParserAsync(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var parser = NetSse.SseParser.Create(ms);
        var count = 0;
        await foreach (var _ in parser.EnumerateAsync())
        {
            count++;
        }
        return count;
    }

    private static int CountEventsUsingTracker(byte[] bytes, int chunkSize)
    {
        var count = 0;
        var tracker = new SseEventBoundaryTracker(() => count++);
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, bytes.Length - i);
            tracker.ProcessBytes(bytes.AsSpan(i, len));
        }
        return count;
    }

    #endregion

    #region Test Dummies

    private sealed class DisposableTrackingContent : HttpContent
    {
        private readonly Stream? _stream;
        private readonly bool _throwOnReadStream;
        private readonly bool _cancelOnReadStream;

        public bool IsDisposed { get; private set; }

        public DisposableTrackingContent(Stream stream)
        {
            _stream = stream;
        }

        public DisposableTrackingContent(bool throwOnReadStream = false, bool cancelOnReadStream = false)
        {
            _throwOnReadStream = throwOnReadStream;
            _cancelOnReadStream = cancelOnReadStream;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            if (_throwOnReadStream)
            {
                throw new IOException("Simulated network stream read failure during acquisition");
            }
            if (_cancelOnReadStream)
            {
                throw new OperationCanceledException();
            }
            return Task.FromResult(_stream ?? new MemoryStream());
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
        }
    }

    private sealed class DisposableTrackingStream : Stream
    {
        private readonly Stream _inner;
        public bool IsDisposed { get; private set; }

        public DisposableTrackingStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
            _inner.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            IsDisposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ControllableStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _currentOffset;
        private TaskCompletionSource _waitingToRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForReadAsync(TimeSpan? timeout = null)
        {
            TaskCompletionSource tcs;
            lock (_channel)
            {
                tcs = _waitingToRead;
            }
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }

        public void Push(string sseText)
        {
            lock (_channel)
            {
                if (_waitingToRead.Task.IsCompleted)
                {
                    _waitingToRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(sseText));
        }

        public void Complete()
        {
            _channel.Writer.Complete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                lock (_channel)
                {
                    _waitingToRead.TrySetResult();
                }

                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
                if (_channel.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _currentOffset = 0;
                }
            }

            var available = _currentChunk.Length - _currentOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
            _currentOffset += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisposableTrackingHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
        }
    }

    #endregion
}
