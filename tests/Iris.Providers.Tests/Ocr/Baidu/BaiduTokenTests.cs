using System.Net;
using Iris.Core.Errors;
using Shouldly;
using static Iris.Providers.Tests.Ocr.Baidu.BaiduTestScope;

namespace Iris.Providers.Tests.Ocr.Baidu;

public sealed class BaiduTokenTests
{
    [Fact]
    public Task ProfileDeadline_IncludesTimeBeforeBackgroundStart() => Run(async f =>
    {
        f.Time.AfterTimestampRead = () => { f.Time.AfterTimestampRead = null; f.Time.Advance(TimeSpan.FromSeconds(1)); };
        var gate = f.Gate(out var wait);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (_, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            await wait(); return f.Token();
        };
        var task = f.Recognize(expected: typeof(IrisException));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        f.Time.Advance(TimeSpan.FromSeconds(4));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10)); task.IsCompleted.ShouldBeFalse();
        gate.Release(); (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.Timeout);
    }, Profile() with { TimeoutSeconds = 5 });

    [Theory]
    [InlineData("not-json")][InlineData("null")][InlineData("{\"error\":false}")]
    [InlineData("{\"error\":\"x\",\"ERROR\":\"y\"}")]
    public Task TokenHttpFailure_WithoutSafeOAuthUsesStatusMapping(string body) => Run(async f =>
    {
        f.Respond = (_, _) => Task.FromResult(f.Json(body, HttpStatusCode.BadRequest));
        (await f.Provider.TestAsync(default)).Error!.Code.ShouldBe(ErrorCode.BadRequest);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task ConcurrentCalls_ShareOneNewToken(bool test) => Run(async f =>
    {
        var gate = f.Gate(out var wait);
        f.Respond = async (r, _) => { if (IsToken(r)) { await wait(); return f.Token(); } return f.Json("{\"words_result\":[]}"); };
        var calls = Enumerable.Range(0, 8).Select(_ => test ? (Task)f.Track(f.Provider.TestAsync(default)) : f.Recognize()).ToArray();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => f.Provider.TokenWaiterCount == 7);
        f.Requests.Count.ShouldBe(1); gate.Release(); await Task.WhenAll(calls);
        f.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).ShouldBe(1);
        if (test) f.Requests.Count.ShouldBe(1);
    });

    [Fact]
    public Task Test_RefreshesOldCacheButConcurrentTestsReuseNewReceipt() => Run(async f =>
    {
        await f.Recognize();
        var gate = f.Gate(out var wait); f.Respond = async (r, _) => { IsToken(r).ShouldBeTrue(); await wait(); return f.Token(); };
        var a = f.Track(f.Provider.TestAsync(default)); var b = f.Track(f.Provider.TestAsync(default));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); await Until(() => f.Provider.TokenWaiterCount == 1);
        gate.Release(); (await a).Success.ShouldBeTrue(); (await b).Sample.ShouldBeNull();
        f.Requests.Count.ShouldBe(3);
    });

    [Theory]
    [InlineData(60L, 53, 1)][InlineData(2592000L, 2505599, 1)][InlineData(long.MaxValue, 2505599, 1)]
    public Task Expiry_UsesMonotonicShortTtlAndClampsLargeValues(long ttl, int reuseSeconds, int refreshSeconds) => Run(async f =>
    {
        f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token(expiry: ttl) : f.Json("{\"words_result\":[]}"));
        await f.Recognize(); f.Time.Advance(TimeSpan.FromSeconds(reuseSeconds)); await f.Recognize();
        f.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).ShouldBe(1);
        f.Time.Advance(TimeSpan.FromSeconds(refreshSeconds)); await f.Recognize();
        f.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).ShouldBe(2);
    });

    [Fact]
    public Task LateOldAuthenticationFailure_DoesNotInvalidateSameStringNewReceipt() => Run(async f =>
    {
        await f.Recognize();
        var gate = f.Gate(out var wait); int ocr = 0;
        f.Respond = async (r, _) =>
        {
            if (IsToken(r)) return f.Token(); // Same bytes, a different published receipt.
            int n = Interlocked.Increment(ref ocr);
            if (n == 1) { await wait(); return f.Json("{\"error_code\":110}"); }
            return f.Json(n == 2 ? "{\"error_code\":111}" : "{\"words_result\":[]}");
        };
        var old = f.Recognize(); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Recognize(); gate.Release(); await old;
        f.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).ShouldBe(2);
        ocr.ShouldBe(4);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task GetterOrWaiterCancellation_IsIsolatedAndGetterDrains(bool cancelGetter) => Run(async f =>
    {
        using var getter = new CancellationTokenSource(); using var waiter = new CancellationTokenSource();
        int tokens = 0; var gate = f.Gate(out var wait);
        f.Respond = async (r, _) => { if (IsToken(r)) { if (Interlocked.Increment(ref tokens) == 1) await wait(); return f.Token(); } return f.Json("{\"words_result\":[]}"); };
        var first = f.Recognize(expected: cancelGetter ? typeof(OperationCanceledException) : null, token: getter.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var second = f.Recognize(expected: cancelGetter ? null : typeof(OperationCanceledException), token: waiter.Token);
        await Until(() => f.Provider.TokenWaiterCount == 1);
        if (cancelGetter)
        {
            getter.Cancel(); first.IsCompleted.ShouldBeFalse(); second.IsCompleted.ShouldBeFalse();
            gate.Release(); await Should.ThrowAsync<OperationCanceledException>(() => first); await second; tokens.ShouldBe(2);
        }
        else
        {
            waiter.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
            first.IsCompleted.ShouldBeFalse(); gate.Release(); await first; tokens.ShouldBe(1);
        }
    });

    [Theory]
    [InlineData("{}")][InlineData("[]")][InlineData("{\"access_token\":\"\",\"expires_in\":60}")]
    [InlineData("{\"access_token\":\"x\",\"expires_in\":0}")][InlineData("{\"access_token\":\"x\",\"expires_in\":1.5}")]
    [InlineData("{\"access_token\":\"x\\n\",\"expires_in\":60}")][InlineData("{\"access_token\":\"x\",\"ACCESS_TOKEN\":\"y\",\"expires_in\":60}")]
    public Task InvalidTokenResponse_FailsSafelyAndDoesNotPoisonCache(string json) => Run(async f =>
    {
        f.Respond = (_, _) => Task.FromResult(f.Json(json));
        var result = await f.Provider.TestAsync(default); result.Success.ShouldBeFalse(); result.Error!.Code.ShouldBe(ErrorCode.InvalidResponse);
        f.Respond = null; (await f.Provider.TestAsync(default)).Success.ShouldBeTrue();
    });

    [Theory]
    [InlineData(200)][InlineData(400)]
    public Task OAuthError_HasPriorityAndNeverLeaksDescription(int status) => Run(async f =>
    {
        f.Respond = (_, _) => Task.FromResult(f.Json("{\"error\":\"invalid_client\",\"error_description\":\"private-oauth\",\"access_token\":false}", (HttpStatusCode)status));
        var result = await f.Provider.TestAsync(default); result.Error!.Code.ShouldBe(ErrorCode.AuthenticationFailed);
        result.Error.Detail!.ShouldNotContain("private-oauth");
    });

    [Fact]
    public Task TokenExpiredWhileInFlight_IsNotPublished() => Run(async f =>
    {
        var gate = f.Gate(out var wait); f.Respond = async (_, _) => { await wait(); return f.Token(expiry: 1); };
        var task = f.Track(f.Provider.TestAsync(default)); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        f.Time.Advance(TimeSpan.FromSeconds(1)); gate.Release();
        (await task).Error!.Code.ShouldBe(ErrorCode.InvalidResponse);
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task MixedBusinessFailures_ShareOneAuthenticationAndOneRateBudget(bool authFirst) => Run(async f =>
    {
        int ocr = 0; f.Respond = (r, _) => Task.FromResult(IsToken(r) ? f.Token() : f.Json($"{{\"error_code\":{(Interlocked.Increment(ref ocr) == 2 ? authFirst ? 18 : 110 : authFirst ? 111 : 18)}}}"));
        var task = f.Recognize(expected: typeof(IrisException));
        await f.Time.TimerCreated(TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(10)); f.Time.Advance(TimeSpan.FromMilliseconds(500));
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(authFirst ? ErrorCode.AuthenticationFailed : ErrorCode.RateLimited);
        ocr.ShouldBe(3); f.Requests.Count.ShouldBe(5);
    });

    [Fact]
    public Task ProfileTimeout_IsNotResetByAuthenticationOrRateRetries() => Run(async f =>
    {
        int ocr = 0;
        f.Respond = (r, _) =>
        {
            if (IsToken(r)) return Task.FromResult(f.Token());
            if (Interlocked.Increment(ref ocr) == 1) return Task.FromResult(f.Json("{\"error_code\":110}"));
            f.Time.Advance(TimeSpan.FromMilliseconds(4800)); return Task.FromResult(f.Json("{\"error_code\":18}"));
        };
        var task = f.Recognize(expected: typeof(IrisException));
        await f.Time.TimerCreated(TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(10)); f.Time.Advance(TimeSpan.FromMilliseconds(500));
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.Timeout); ocr.ShouldBe(2);
    }, Profile() with { TimeoutSeconds = 5 });

    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
}
