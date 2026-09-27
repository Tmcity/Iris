using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Languages;
using Iris.Core.Translation;
using Iris.Providers.Http;
using Iris.Providers.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using Beta = Anthropic.Models.Beta.Messages;

namespace Iris.Providers.Tests.Translation;

public sealed class ProviderCallGuardTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private const string ProviderId = "test-provider";

    // 1. 延迟执行
    [Fact]
    public async Task GuardStreamAsync_ExecutionIsDeferred_CoreNotCalledUntilMoveNextAsync()
    {
        var coreCalled = false;
        async IAsyncEnumerable<TranslationChunk> Core([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Action reset)
        {
            coreCalled = true;
            await Task.Yield();
            yield break;
        }

        var stream = ProviderCallGuard.GuardStreamAsync(
            Core,
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: null,
            CancellationToken.None);

        coreCalled.ShouldBeFalse();

        await foreach (var _ in stream)
        {
        }

        coreCalled.ShouldBeTrue();
    }

    // 2. 用户取消优先于超时
    [Fact]
    public async Task GuardStreamAsync_UserCancellation_TakesPrecedenceOverTimeout()
    {
        using var userCts = new CancellationTokenSource();

        async IAsyncEnumerable<TranslationChunk> Core([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Action reset)
        {
            await Task.Yield();
            userCts.Cancel();
            _timeProvider.Advance(TimeSpan.FromSeconds(20)); // 超时也触发
            ct.ThrowIfCancellationRequested();
            yield return new TranslationChunk("chunk");
        }

        var stream = ProviderCallGuard.GuardStreamAsync(
            Core,
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: null,
            userCts.Token);

        var ex = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        ex.CancellationToken.ShouldBe(userCts.Token);
    }

    // 3. 超时归类为 IrisException(Timeout)
    [Fact]
    public async Task GuardStreamAsync_Timeout_ThrowsIrisExceptionWithTimeoutCode()
    {
        async IAsyncEnumerable<TranslationChunk> Core([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Action reset)
        {
            await Task.Yield();
            _timeProvider.Advance(TimeSpan.FromSeconds(11));
            ct.ThrowIfCancellationRequested();
            yield return new TranslationChunk("chunk");
        }

        var stream = ProviderCallGuard.GuardStreamAsync(
            Core,
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: null,
            CancellationToken.None);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
        ex.ProviderId.ShouldBe(ProviderId);
    }

    // 4. resetTimeout 能够推迟超时
    [Fact]
    public async Task GuardStreamAsync_ResetTimeout_PostponesTimeout()
    {
        async IAsyncEnumerable<TranslationChunk> Core([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Action reset)
        {
            await Task.Yield();
            yield return new TranslationChunk("1");
            reset();
            _timeProvider.Advance(TimeSpan.FromSeconds(6));

            yield return new TranslationChunk("2");
            reset();
            _timeProvider.Advance(TimeSpan.FromSeconds(6));

            yield return new TranslationChunk("3");
            reset();
            _timeProvider.Advance(TimeSpan.FromSeconds(6));
        }

        var stream = ProviderCallGuard.GuardStreamAsync(
            Core,
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: null,
            CancellationToken.None);

        var chunks = new List<TranslationChunk>();
        await foreach (var c in stream)
        {
            chunks.Add(c);
        }

        chunks.Count.ShouldBe(3);
    }

    // 5. 原生 IrisException 直接原样抛出，不经过 mapSpecific
    [Fact]
    public async Task GuardStreamAsync_IrisException_ThrownDirectlyWithoutCallingMapSpecific()
    {
        var mapSpecificCalled = false;
        var originalEx = new IrisException(ErrorCode.AuthenticationFailed, "Invalid Key", providerId: ProviderId);

        var stream = ProviderCallGuard.GuardStreamAsync(
            (ct, reset) => ThrowAsync<TranslationChunk>(originalEx),
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: ex =>
            {
                mapSpecificCalled = true;
                return new IrisException(ErrorCode.BadRequest, "Re-mapped");
            },
            CancellationToken.None);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        mapSpecificCalled.ShouldBeFalse();
        ex.ShouldBeSameAs(originalEx);
    }

    // 6. mapSpecific 位于 IrisException 之后、TryMapException 之前
    [Fact]
    public async Task GuardStreamAsync_MapSpecific_CalledBeforeTryMapException()
    {
        var expectedEx = new IrisException(ErrorCode.InvalidResponse, "Mapped from custom", providerId: ProviderId);

        var stream = ProviderCallGuard.GuardStreamAsync(
            (ct, reset) => ThrowAsync<TranslationChunk>(new HttpRequestException("network failure")),
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: ex => expectedEx,
            CancellationToken.None);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        ex.ShouldBeSameAs(expectedEx);
    }

    // 7. mapSpecific 返回 null 时继续进入 TryMapException
    [Fact]
    public async Task GuardStreamAsync_MapSpecificReturnsNull_FallsThroughToTryMapException()
    {
        var stream = ProviderCallGuard.GuardStreamAsync(
            (ct, reset) => ThrowAsync<TranslationChunk>(new HttpRequestException("network down")),
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: ex => null,
            CancellationToken.None);

        var ex = await Should.ThrowAsync<IrisException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        ex.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        ex.Retryable.ShouldBeTrue();
        ex.ProviderId.ShouldBe(ProviderId);
    }

    // 8. 全部映射器未处理时抛出原异常
    [Fact]
    public async Task GuardStreamAsync_UnmappedException_ThrownAsOriginal()
    {
        var original = new FormatException("weird format");

        var stream = ProviderCallGuard.GuardStreamAsync(
            (ct, reset) => ThrowAsync<TranslationChunk>(original),
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: ex => null,
            CancellationToken.None);

        var ex = await Should.ThrowAsync<FormatException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        ex.ShouldBeSameAs(original);
    }

    // 9. GuardAsync 总超时与异常归类
    [Fact]
    public async Task GuardAsync_Timeout_ThrowsIrisExceptionWithTimeoutCode()
    {
        var task = ProviderCallGuard.GuardAsync<string>(
            async ct =>
            {
                await Task.Yield();
                _timeProvider.Advance(TimeSpan.FromSeconds(15));
                ct.ThrowIfCancellationRequested();
                return "ok";
            },
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: null,
            CancellationToken.None);

        var ex = await Should.ThrowAsync<IrisException>(async () => await task);
        ex.Code.ShouldBe(ErrorCode.Timeout);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task GuardAsync_UserCancellation_ThrowsOperationCanceledException()
    {
        using var userCts = new CancellationTokenSource();
        userCts.Cancel();

        var task = ProviderCallGuard.GuardAsync<string>(
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("ok");
            },
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: null,
            userCts.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task GuardAsync_MapSpecific_MapsCorrectly()
    {
        var mapped = new IrisException(ErrorCode.RateLimited, "rate limited", providerId: ProviderId);

        var task = ProviderCallGuard.GuardAsync<string>(
            ct => Task.FromException<string>(new NotSupportedException("rate limit hit")),
            TimeSpan.FromSeconds(10),
            _timeProvider,
            ProviderId,
            mapSpecific: ex => mapped,
            CancellationToken.None);

        var ex = await Should.ThrowAsync<IrisException>(async () => await task);
        ex.ShouldBeSameAs(mapped);
    }

    // 10. RunTestAsync 的四种结果
    [Fact]
    public async Task RunTestAsync_Success_ReturnsSampleAndLatency()
    {
        var provider = Substitute.For<ITranslationProvider>();
        provider.Descriptor.Returns(new ProviderDescriptor(ProviderId, "Test", "TestKind", "m1", true));
        provider.TranslateStreamAsync(Arg.Any<ProviderTranslateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable([new TranslationChunk("Hello"), new TranslationChunk(" World")]));

        var logger = Substitute.For<ILogger>();

        var result = await ProviderCallGuard.RunTestAsync(provider, "zh-CN", _timeProvider, logger, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Sample.ShouldBe("Hello World");
        result.Error.ShouldBeNull();
        result.Latency.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunTestAsync_UserCancellation_ThrowsOperationCanceledException()
    {
        // 1. Upfront cancellation
        using var userCts = new CancellationTokenSource();
        userCts.Cancel();

        var provider = Substitute.For<ITranslationProvider>();
        provider.Descriptor.Returns(new ProviderDescriptor(ProviderId, "Test", "TestKind", "m1", true));

        var logger = Substitute.For<ILogger>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await ProviderCallGuard.RunTestAsync(provider, "zh-CN", _timeProvider, logger, userCts.Token);
        });

        // 2. Mid-stream cancellation
        using var userCts2 = new CancellationTokenSource();
        var provider2 = Substitute.For<ITranslationProvider>();
        provider2.Descriptor.Returns(new ProviderDescriptor(ProviderId, "Test", "TestKind", "m1", true));
        provider2.TranslateStreamAsync(Arg.Any<ProviderTranslateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ThrowAsync<TranslationChunk>(new OperationCanceledException(userCts2.Token)));
        userCts2.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await ProviderCallGuard.RunTestAsync(provider2, "zh-CN", _timeProvider, logger, userCts2.Token);
        });
    }

    [Fact]
    public async Task RunTestAsync_IrisException_ReturnsSuccessFalseWithErrorInfo()
    {
        var irisEx = new IrisException(ErrorCode.AuthenticationFailed, "Invalid Key", providerId: ProviderId, detail: "Key revoked");
        var provider = Substitute.For<ITranslationProvider>();
        provider.Descriptor.Returns(new ProviderDescriptor(ProviderId, "Test", "TestKind", "m1", true));
        provider.TranslateStreamAsync(Arg.Any<ProviderTranslateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ThrowAsync<TranslationChunk>(irisEx));

        var logger = Substitute.For<ILogger>();

        var result = await ProviderCallGuard.RunTestAsync(provider, "zh-CN", _timeProvider, logger, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(ErrorCode.AuthenticationFailed);
        result.Error.ProviderId.ShouldBe(ProviderId);
        result.Error.Detail.ShouldBe("Key revoked");
    }

    [Fact]
    public async Task RunTestAsync_OtherException_LogsWarningWithoutExceptionObjectAndReturnsUnknownError()
    {
        const string secretMessage = "VerySecretPassword-DoNotLog";
        var otherEx = new InvalidOperationException(secretMessage);

        var provider = Substitute.For<ITranslationProvider>();
        provider.Descriptor.Returns(new ProviderDescriptor(ProviderId, "Test", "TestKind", "m1", true));
        provider.TranslateStreamAsync(Arg.Any<ProviderTranslateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ThrowAsync<TranslationChunk>(otherEx));

        var fakeCollector = new FakeLogCollector();
        using var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(new FakeLoggerProvider(fakeCollector));
        var logger = loggerFactory.CreateLogger("TestCategory");

        var result = await ProviderCallGuard.RunTestAsync(provider, "zh-CN", _timeProvider, logger, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(ErrorCode.Unknown);
        result.Error.Detail.ShouldBe("InvalidOperationException");
        result.Error.ProviderId.ShouldBe(ProviderId);

        var logs = fakeCollector.GetSnapshot();
        logs.Count.ShouldBe(1);
        var log = logs[0];
        log.Level.ShouldBe(LogLevel.Warning);
        log.Message.ShouldContain(ProviderId);
        log.Message.ShouldContain("InvalidOperationException");
        log.Message.ShouldNotContain(secretMessage);
        log.Exception.ShouldBeNull(); // 关键安全断言：不传入异常对象
    }

    // ---------------- 辅助方法 ----------------

    private static async IAsyncEnumerable<T> ThrowAsync<T>(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // Unreachable code detected
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            yield return item;
        }
    }
}
