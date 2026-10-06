using System.Runtime.CompilerServices;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Common;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using static Iris.Core.Tests.Ocr.OcrPipelineTestScope;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrPipelineLifecycleTests
{
    [Fact]
    public Task FailedCleanupRetainsOnlySnapshot_ThenRetryReleasesItOnLiveService() => Run(async f =>
    {
        var registry = new EphemeralRegistry(f.Lease.Settings, f.Lease.Providers);
        await using var service = new OcrService(registry, f.Images, new OcrTextNormalizer());
        try
        {
            var refs = await FailAndForget(f, service); f.Prepared.Clear(); f.Main.Requests.Clear();
            for (int i = 0; i < 5 && refs.Any(r => r.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
            refs.ShouldAllBe(r => !r.IsAlive); registry.Snapshot!.IsAlive.ShouldBeTrue();
            registry.Fail = false; await service.RetryCleanupAsync();
            for (int i = 0; i < 5 && registry.Snapshot.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
            registry.Snapshot.IsAlive.ShouldBeFalse(); GC.KeepAlive(service);
        }
        finally { registry.Fail = false; await service.StopAsync(); }
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> FailAndForget(OcrPipelineTestScope f, OcrService service)
    {
        WeakReference? raw = null; f.Main.Recognize = (_, _) =>
        {
            var value = new OcrResult("main", [new(new string('x', 70))], null, TimeSpan.Zero); raw = new(value); return Task.FromResult(value);
        };
        var image = f.Image with { Bytes = new byte[4096] };
        await Should.ThrowAsync<IrisException>(() => service.RecognizeAsync(image)); f.Main.Recognize = null;
        return [new(image), new(image.Bytes), raw!];
    }
    private sealed class EphemeralRegistry(AppSettings settings, IReadOnlyList<OcrProviderRegistration> providers) : IOcrProviderRegistry
    {
        internal bool Fail = true; internal WeakReference? Snapshot;
        public Task<IOcrProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
        { var item = new EphemeralSnapshot(this, settings, providers); Snapshot = new(item); return Task.FromResult<IOcrProviderSnapshot>(item); }
        public Task<IOcrProviderSnapshot> CreateTransientAsync(OcrProviderProfile profile, ProviderCredentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RetryCleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StopAccepting() { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event EventHandler? Changed { add { } remove { } }
        private sealed class EphemeralSnapshot(EphemeralRegistry owner, AppSettings settings, IReadOnlyList<OcrProviderRegistration> providers) : IOcrProviderSnapshot
        {
            public AppSettings Settings => settings;
            public IReadOnlyList<OcrProviderRegistration> Providers => providers;
            public ValueTask DisposeAsync() { if (owner.Fail) throw new IOException("private cleanup"); return ValueTask.CompletedTask; }
        }
    }
    [Fact]
    public Task MultipleSnapshotFailuresAreAllRetriedWithoutBusinessReplay() => Run(async f =>
    {
        var second = new Snapshot { Settings = f.Lease.Settings, Providers = f.Lease.Providers, Fail = true };
        f.Releases.Add(() => second.Fail = false); f.Lease.Fail = true;
        int acquire = 0;
        f.Registry.AcquireSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IOcrProviderSnapshot>(Interlocked.Increment(ref acquire) == 1 ? f.Lease : second));
        await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)));
        await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)));
        await Should.ThrowAsync<IrisException>(() => f.Service.RetryCleanupAsync());
        f.Lease.Calls.ShouldBe(2); second.Calls.ShouldBe(2);
        f.Lease.Fail = false; await Should.ThrowAsync<IrisException>(() => f.Service.StopAsync());
        f.Lease.Calls.ShouldBe(3); second.Calls.ShouldBe(3);
        second.Fail = false; await f.Service.StopAsync(); second.Calls.ShouldBe(4); f.Lease.Calls.ShouldBe(3);
        acquire.ShouldBe(2); f.Main.Requests.Count.ShouldBe(2);
    });
    [Fact]
    public Task CanceledStopWaiterCannotAbandonSharedStop() => Run(async f =>
    {
        var gate = f.Gate(); f.Main.Recognize = async (_, _) => { await gate.Wait(); return f.Main.Result; };
        var work = f.Start(typeof(OperationCanceledException)); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        using var waiter = new CancellationTokenSource(); var canceled = f.Service.StopAsync(waiter.Token); var stop = f.Service.StopAsync();
        waiter.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => canceled);
        stop.IsCompleted.ShouldBeFalse(); gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => work); await stop;
        f.Lease.Calls.ShouldBe(1);
    });
    [Theory][InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public Task CancelAndStopWaitActualNonCooperativeTasks(bool recognition, bool stop) => Run(async f =>
    {
        var gate = f.Gate(); using var caller = new CancellationTokenSource();
        if (recognition) f.Main.Recognize = async (_, _) => { await gate.Wait(); return f.Main.Result; };
        else f.Prepare = async (image, _, _) => { await gate.Wait(); return image; };
        var work = f.Start(typeof(OperationCanceledException), token: caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Task? stopping = null;
        if (stop) { f.Service.StopAccepting(); stopping = f.Service.StopAsync(); }
        else caller.Cancel();
        work.IsCompleted.ShouldBeFalse(); f.Lease.Calls.ShouldBe(0);
        if (stopping != null) { stopping.IsCompleted.ShouldBeFalse(); Should.Throw<IrisException>(f.Service.Dispose); }
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => work);
        if (stopping != null) await stopping;
        f.Lease.Calls.ShouldBe(1); f.Fallback.Requests.ShouldBeEmpty();
    });
    [Fact]
    public Task CancellationBeforeFallbackDoesNotRetry() => Run(async f =>
    {
        using var caller = new CancellationTokenSource();
        f.Main.Recognize = (_, _) => { caller.Cancel(); throw new IrisException(ErrorCode.Timeout, "safe"); };
        await Should.ThrowAsync<OperationCanceledException>(() => f.Start(typeof(OperationCanceledException), token: caller.Token));
        f.Fallback.Requests.ShouldBeEmpty();
    });
    [Fact]
    public Task SuccessWaitsSnapshotRelease_AndCancellationCannotReturnResult() => Run(async f =>
    {
        var gate = f.Gate(); f.Lease.Before = gate.Wait; using var caller = new CancellationTokenSource();
        var work = f.Start(typeof(OperationCanceledException), token: caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10)); work.IsCompleted.ShouldBeFalse(); caller.Cancel();
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => work);
    });
    [Theory][InlineData(false)][InlineData(true)]
    public Task FailedSnapshotRetainsOriginal_ConcurrentRetryAndStopDoNotRecognizeAgain(bool primary) => Run(async f =>
    {
        f.Lease.Fail = true;
        if (primary) f.Main.Recognize = (_, _) => throw new IrisException(ErrorCode.InvalidResponse, "private primary");
        var failure = await Should.ThrowAsync<IrisException>(() => f.Start(typeof(IrisException)));
        failure.Code.ShouldBe(ErrorCode.Unknown); failure.ToString().ShouldNotContain("private"); failure.InnerException.ShouldBeNull();
        var gate = f.Gate(); f.Lease.Fail = false; f.Lease.Before = gate.Wait;
        using var caller = new CancellationTokenSource();
        var a = f.Service.RetryCleanupAsync(caller.Token);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var b = f.Service.RetryCleanupAsync(); var stop = f.Service.StopAsync();
        caller.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => a);
        b.IsCompleted.ShouldBeFalse(); stop.IsCompleted.ShouldBeFalse(); f.Lease.Calls.ShouldBe(2);
        gate.Open(); await b; await stop; f.Lease.Calls.ShouldBe(2); f.Main.Requests.Count.ShouldBe(1); f.Prepared.Count.ShouldBe(1);
    });
    [Fact]
    public Task ThrowingCancellationCallbacksAreAwaitedAndSanitized() => Run(async f =>
    {
        var gate = f.Gate(); var callback = f.Gate(); CancellationTokenRegistration registration = default;
        f.Main.Recognize = async (_, token) =>
        {
            registration = token.Register(() => { callback.Wait().GetAwaiter().GetResult(); throw new IOException("private callback"); });
            await gate.Wait(); return f.Main.Result;
        };
        var work = f.Start(typeof(IrisException)); await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        f.Service.StopAccepting(); var stop = f.Service.StopAsync();
        await callback.Entered.WaitAsync(TimeSpan.FromSeconds(10)); gate.Open();
        stop.IsCompleted.ShouldBeFalse(); work.IsCompleted.ShouldBeFalse(); callback.Open();
        (await Should.ThrowAsync<IrisException>(() => work)).ToString().ShouldNotContain("private");
        await Should.ThrowAsync<IrisException>(() => stop); await registration.DisposeAsync();
        f.Lease.Calls.ShouldBe(1); await f.Service.StopAsync();
    });
    [Fact]
    public Task CompletedServiceDoesNotRetainImagesOrOutcome() => Run(async f =>
    {
        var refs = await Complete(f); f.Main.Requests.Clear(); f.Prepared.Clear();
        for (int i = 0; i < 5 && refs.Any(r => r.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        refs.ShouldAllBe(r => !r.IsAlive); var positive = new WeakReference(f.Service); positive.IsAlive.ShouldBeTrue(); GC.KeepAlive(f.Service);
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> Complete(OcrPipelineTestScope f)
    {
        var input = f.Image with { Bytes = new byte[4096] };
        var result = await f.Service.RecognizeAsync(input);
        return [new(input), new(input.Bytes), new(result), new(new byte[4096])];
    }
}
