using System.Runtime.CompilerServices;
using Iris.Core.Errors;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shouldly;
using static Iris.Providers.Tests.Ocr.Baidu.BaiduTestScope;

namespace Iris.Providers.Tests.Ocr.Baidu;

public sealed class BaiduOwnershipTests
{
    [Fact]
    public Task CancellationCallbackPrimaryAndClientCleanupFailures_AreAllPreserved() => Run(async f =>
    {
        await f.Provider.TestAsync(default); using var caller = new CancellationTokenSource();
        var gate = f.Gate(out var wait); CancellationTokenRegistration registration = default;
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (_, token) =>
        {
            registration = token.Register(() => { cancellationEntered.TrySetResult(); throw new IOException("private callback", unchecked((int)0x81230004)); });
            await wait(); throw new IOException("private send", unchecked((int)0x81230001));
        };
        f.FailClientDispose = true;
        var task = f.Recognize(expected: typeof(IrisException), token: caller.Token);
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); gate.Release();
            var error = await Should.ThrowAsync<IrisException>(() => task);
            error.Detail!.ShouldContain("81230001"); error.Detail!.ShouldContain("CLIENT"); error.Detail!.ShouldContain("CANCEL"); error.Detail!.ShouldContain("81230004");
            error.Detail!.ShouldNotContain("private"); task.IsCanceled.ShouldBeFalse();
        }
        finally { gate.Release(); await registration.DisposeAsync(); }
    });

    [Fact]
    public Task HttpFailureArrivingAfterCancellation_IsNotHidden() => Run(async f =>
    {
        await f.Provider.TestAsync(default); using var caller = new CancellationTokenSource();
        var gate = f.Gate(out var wait);
        f.Respond = async (_, _) => { await wait(); return f.Json("private error", System.Net.HttpStatusCode.ServiceUnavailable); };
        var task = f.Recognize(expected: typeof(IrisException), token: caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); gate.Release();
        (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.ProviderUnavailable);
    });

    [Fact]
    public Task ActualMessagesAndContents_AreDisposedAndElapsedIncludesCleanup() => Run(async f =>
    {
        await f.Provider.TestAsync(default);
        HttpRequestMessage? message = null; HttpContent? content = null;
        f.Respond = (r, _) => { message = r; content = r.Content; return Task.FromResult(f.Json("{\"words_result\":[]}")); };
        f.BeforeStreamDispose = () => f.Time.Advance(TimeSpan.FromMilliseconds(10));
        var result = await f.Recognize(); result.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(10));
        Should.Throw<ObjectDisposedException>(() => message!.Method = HttpMethod.Get);
        await Should.ThrowAsync<ObjectDisposedException>(() => content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    });

    [Theory]
    [InlineData(false, 0)][InlineData(false, 1)][InlineData(false, 2)]
    [InlineData(true, 0)][InlineData(true, 1)][InlineData(true, 2)]
    public Task CancellationStopAndTimeout_WaitForActualSendOrRead(bool read, int mode) => Run(async f =>
    {
        await f.Provider.TestAsync(default);
        var gate = f.Gate(out var wait); using var caller = new CancellationTokenSource();
        if (read) f.BeforeRead = wait;
        else f.Respond = async (_, _) => { await wait(); return f.Json("{\"words_result\":[]}"); };
        var task = f.Recognize(expected: mode == 2 ? typeof(IrisException) : typeof(OperationCanceledException), token: caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Should.Throw<IrisException>(f.Provider.Dispose);
        Task? disposal = null;
        if (mode == 0) caller.Cancel();
        else if (mode == 1) { disposal = f.Provider.DisposeAsync().AsTask(); f.Provider.DisposeAsync().AsTask().ShouldBeSameAs(disposal); }
        else f.Time.Advance(TimeSpan.FromSeconds(5));
        task.IsCompleted.ShouldBeFalse(); if (disposal != null) disposal.IsCompleted.ShouldBeFalse();
        gate.Release();
        if (mode == 2) (await Should.ThrowAsync<IrisException>(() => task)).Code.ShouldBe(ErrorCode.Timeout);
        else await Should.ThrowAsync<OperationCanceledException>(() => task);
        if (disposal != null) await disposal;
        f.Streams.ShouldAllBe(s => s.Released); f.Clients.ShouldAllBe(c => c.Released);
    }, Profile() with { TimeoutSeconds = 5 });

    [Theory]
    [InlineData(0, ErrorCode.Unknown)][InlineData(1, ErrorCode.Timeout)][InlineData(2, ErrorCode.ProviderUnavailable)][InlineData(3, ErrorCode.NetworkUnavailable)]
    public Task TransportFailures_AreClassifiedWithoutExternalText(int kind, ErrorCode expected) => Run(async f =>
    {
        Exception error = kind switch { 0 => new OperationCanceledException("private"), 1 => new TimeoutRejectedException("private"), 2 => new BrokenCircuitException("private"), _ => new HttpRequestException("private") };
        f.Respond = (_, _) => Task.FromException<System.Net.Http.HttpResponseMessage>(error);
        var result = await f.Provider.TestAsync(default); result.Error!.Code.ShouldBe(expected); result.Error.Detail!.ShouldNotContain("private");
    });

    [Fact]
    public Task PrimaryStreamAndClientFailures_RetryOriginalResourcesAfterDisposedFlags() => Run(async f =>
    {
        await f.Provider.TestAsync(default);
        f.ReadFailure = new IOException("private read", unchecked((int)0x81230001));
        f.FailStreamDispose = f.FailClientDispose = true;
        var error = await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)));
        foreach (var value in new[] { "81230001", "81230002", "81230003", "RESPONSE_CONTENT", "BODY_STREAM", "CLIENT" }) error.Detail!.ShouldContain(value);
        error.Detail!.ShouldNotContain("private"); error.InnerException.ShouldBeNull();
        var stream = f.Streams.Last(); var client = f.Clients.Last(); stream.Released.ShouldBeFalse(); client.Released.ShouldBeFalse();
        await Should.ThrowAsync<IrisException>(() => f.Provider.RetryCleanupAsync()); f.Requests.Count.ShouldBe(2);
        await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)));
        f.Requests.Count.ShouldBe(2); f.Clients.Count.ShouldBe(2);
        stream.Released.ShouldBeFalse();
        f.ReadFailure = null; f.FailStreamDispose = f.FailClientDispose = false;
        await f.Provider.RetryCleanupAsync(); stream.Released.ShouldBeTrue(); client.Released.ShouldBeTrue(); f.Requests.Count.ShouldBe(2);
        await f.Recognize(); f.Requests.Count.ShouldBe(3);
    });

    [Fact]
    public Task TimerCleanupFailure_RetainsOriginalTimerAndCanRetry() => Run(async f =>
    {
        f.Time.FailDispose = true;
        var result = await f.Provider.TestAsync(default); result.Success.ShouldBeFalse(); result.Error!.Detail!.ShouldContain("TIMER");
        await Should.ThrowAsync<IrisException>(() => f.Provider.DisposeAsync().AsTask());
        f.Time.FailDispose = false; await f.Provider.DisposeAsync();
        f.Provider.Dispose();
    });

    [Fact]
    public async Task CancellationDuringDisposal_WaitsForActualRelease()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            await f.Provider.TestAsync(default); using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(release.Set); f.BeforeStreamDispose = () => { entered.TrySetResult(); release.Wait(); };
            var task = f.Recognize(expected: typeof(OperationCanceledException), token: caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); task.IsCompleted.ShouldBeFalse();
            release.Set(); await Should.ThrowAsync<OperationCanceledException>(() => task);
            f.Streams.ShouldAllBe(s => s.Released);
        });
    }

    [Fact]
    public Task UnknownLengthBody_IsLimitedDuringActualRead() => Run(async f =>
    {
        await f.Provider.TestAsync(default); f.UnknownContentLength = true;
        f.Respond = (_, _) => Task.FromResult(f.Json(new string(' ', 16 * 1024 * 1024 + 1)));
        (await Should.ThrowAsync<IrisException>(() => f.Recognize(expected: typeof(IrisException)))).Code.ShouldBe(ErrorCode.InvalidResponse);
        f.Streams.Last().TotalRead.ShouldBe(16 * 1024 * 1024 + 1); f.Streams.Last().Released.ShouldBeTrue();
    });

    [Fact]
    public Task LiveProvider_DoesNotCacheImagesOrResults() => Run(async f =>
    {
        var references = await Complete(f);
        f.Tasks.Clear(); f.Requests.Clear(); await Task.Yield();
        for (int i = 0; i < 5 && references.Any(r => r.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        references.ShouldAllBe(r => !r.IsAlive); GC.KeepAlive(f.Provider);
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> Complete(BaiduTestScope f)
    {
        var input = Input(bytes: new byte[100]); var output = await f.Recognize(input);
        return [new(input), new(input.Image), new(input.Image.Bytes), new(output), new(output.Lines), new(new byte[1024])];
    }
}
