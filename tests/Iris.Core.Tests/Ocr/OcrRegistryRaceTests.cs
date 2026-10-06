using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Security;
using Iris.Core.Settings;
using Shouldly;
using static Iris.Core.Tests.Ocr.OcrRegistryTestScope;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrRegistryRaceTests
{
    [Fact]
    public async Task FactoryInvalidatedDuringConstruction_CleansCandidateBeforePublishingCurrent()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            f.Settings.Value = Config(Profile(name: "old")); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Providers.BeforeCreate = _ => { entered.TrySetResult(); release.Wait(); }; f.Releases.Add(release.Set);
            int changes = 0; f.Registry.Changed += (_, _) => changes++;
            var refresh = f.Refresh(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Settings.UpdateAsync(_ => Config(Profile(name: "new"))); release.Set(); await refresh;
            var snapshot = await f.Acquire(); snapshot.Providers[0].Profile.DisplayName.ShouldBe("new");
            f.Providers.Created.Count.ShouldBe(2); f.Providers.Created.First().Disposed.ShouldBeTrue(); changes.ShouldBe(1);
        });
    }

    [Fact]
    public async Task CredentialAndSettingsChangeDuringRead_NeverPublishesMixedGeneration()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            const string old = "cs-00000000000000000000000000000001", next = "cs-00000000000000000000000000000002";
            f.Settings.Value = Config(Profile() with { CredentialSetId = old });
            f.SecretStore.Values[SecretKeys.CredentialSet(old)] = Payload("old");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.SecretStore.BeforeGet = key => { if (key == SecretKeys.CredentialSet(old)) { entered.TrySetResult(); release.Wait(); } }; f.Releases.Add(release.Set);
            var refresh = f.Refresh(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.SecretStore.Set(SecretKeys.CredentialSet(next), Payload("new"));
            await f.Settings.UpdateAsync(_ => Config(Profile() with { CredentialSetId = next }));
            release.Set(); await refresh;
            var snapshot = await f.Acquire(); snapshot.Providers.Single().Profile.CredentialSetId.ShouldBe(next);
            ((Provider)snapshot.Providers.Single().Provider).Credentials.ApiKey.ShouldBe("new");
            f.Providers.Created.Where(p => p.Credentials.ApiKey == "old").ShouldAllBe(p => p.Disposed);
        });
    }

    [Fact]
    public async Task SharedRefresh_CancelingOneWaiterDoesNotCancelConstructionOrAnotherWaiter()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            f.Settings.Value = Config(Profile()); using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Providers.BeforeCreate = _ => { entered.TrySetResult(); release.Wait(); }; f.Releases.Add(release.Set);
            var first = f.Refresh(typeof(OperationCanceledException), caller.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = f.Refresh(); caller.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => first);
            second.IsCompleted.ShouldBeFalse(); release.Set(); await second; f.Providers.CreateCalls.ShouldBe(1);
            (await f.Acquire()).Providers.Count.ShouldBe(1);
        });
    }

    [Fact]
    public Task CanceledAcquire_GrantsNoOrphanLeaseAtDelivery() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); f.Settings.Value = Config(Profile());
        f.Providers.Configure = p => p.BeforeDescriptor = caller.Cancel;
        await Should.ThrowAsync<OperationCanceledException>(() => f.Acquire(typeof(OperationCanceledException), caller.Token));
        await f.Registry.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); f.Providers.Created.ShouldAllBe(p => p.Disposed);
    });

    [Fact]
    public Task CandidateFailure_CleansOnlyNewInstancesAndNeverReturnsStaleSnapshot() => Run(async f =>
    {
        f.Settings.Value = Config(Profile("a")); var old = await f.Acquire(); var kept = (Provider)old.Providers[0].Provider;
        f.Providers.Configure = p => p.FailDescriptor = p.Profile.Id == "b";
        await f.Settings.UpdateAsync(_ => Config(Profile("a"), Profile("b")));
        await Should.ThrowAsync<IrisException>(() => f.Refresh(typeof(IrisException)));
        await Should.ThrowAsync<IrisException>(() => f.Acquire(typeof(IrisException)));
        kept.Disposed.ShouldBeFalse(); f.Providers.Created.Where(p => p.Profile.Id == "b").ShouldAllBe(p => p.Disposed);
        f.Providers.Configure = null; await f.Refresh(); (await f.Acquire()).Providers.Count.ShouldBe(2);
    });

    [Fact]
    public Task FailedVersion_IsQuietUntilExplicitRetryOrNewInvalidation() => Run(async f =>
    {
        f.Settings.Value = Config(Profile()); f.Providers.Throw = true;
        await Should.ThrowAsync<IrisException>(() => f.Refresh(typeof(IrisException))); int attempts = f.Providers.CreateCalls;
        for (int i = 0; i < 3; i++) await Should.ThrowAsync<IrisException>(() => f.Acquire(typeof(IrisException)));
        f.Providers.CreateCalls.ShouldBe(attempts); f.Providers.Throw = false;
        await f.Refresh(); (await f.Acquire()).Providers.Count.ShouldBe(1);
    });

    [Fact]
    public async Task StopDuringFactory_WaitsReturnedCandidateAndItsRealDisposal()
    {
        using var release = new ManualResetEventSlim();
        await Run(async f =>
        {
            f.Settings.Value = Config(Profile()); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Providers.BeforeCreate = _ => { entered.TrySetResult(); release.Wait(); }; f.Releases.Add(release.Set);
            var refresh = f.Refresh(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = f.Registry.StopAsync(); stop.IsCompleted.ShouldBeFalse(); release.Set();
            await Task.WhenAll(refresh, stop); f.Providers.Created.Single().Disposed.ShouldBeTrue();
        });
    }

    [Fact]
    public Task TransientCancellationAfterFactory_WaitsCleanupAndNeverLeaksLease() => Run(async f =>
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Releases.Add(() => release.TrySetResult());
        f.Providers.Configure = p => { p.BeforeDescriptor = caller.Cancel; p.BeforeDispose = async () => { entered.TrySetResult(); await release.Task; }; };
        var transient = f.Transient(Profile() with { Enabled = false }, ProviderCredentials.None, typeof(OperationCanceledException), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); transient.IsCompleted.ShouldBeFalse(); release.TrySetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => transient);
        await f.Registry.StopAsync(); f.Providers.Created.Single().Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task SnapshotFailure_RepeatReleaseAndRegistryRetryShareOriginalCleanup() => Run(async f =>
    {
        f.Settings.Value = Config(Profile()); var snapshot = await f.Acquire(); var p = (Provider)snapshot.Providers[0].Provider;
        await f.Settings.UpdateAsync(_ => Config()); await f.Refresh(); p.FailDispose = true;
        await Should.ThrowAsync<IrisException>(() => snapshot.DisposeAsync().AsTask());
        Should.Throw<ObjectDisposedException>(() => _ = snapshot.Settings); p.Disposed.ShouldBeFalse(); p.FailDispose = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Releases.Add(() => release.TrySetResult()); p.BeforeDispose = async () => { entered.TrySetResult(); await release.Task; };
        var repeated = snapshot.DisposeAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var registry = f.Registry.RetryCleanupAsync(); p.DisposeCalls.ShouldBe(2); release.TrySetResult();
        await Task.WhenAll(repeated, registry); p.DisposeCalls.ShouldBe(2); p.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task SubscriberFailure_DoesNotHideOtherSubscriberOrNewInvalidation() => Run(async f =>
    {
        f.Settings.Value = Config(Profile()); int calls = 0;
        f.Registry.Changed += (_, _) => throw new IOException("private subscriber");
        f.Registry.Changed += (_, _) => { calls++; if (calls == 1) _ = f.Settings.UpdateAsync(_ => Config(Profile(name: "new"))); };
        await f.Refresh(); var snapshot = await f.Acquire(); snapshot.Providers.Single().Profile.DisplayName.ShouldBe("new"); calls.ShouldBe(2);
    });

    private static string Payload(string key) => $"{{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"{key}\",\"secretKey\":null}}";
}
