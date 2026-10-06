using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using static Iris.Core.Tests.Ocr.OcrRegistryTestScope;

namespace Iris.Core.Tests.Ocr;

public sealed class OcrProviderRegistryTests
{
    [Fact]
    public Task Constructor_IsLazyAndFactoryDuplicationIsSafe() => Run(async f =>
    {
        f.Settings.Reads.ShouldBe(0); f.SecretStore.Reads.ShouldBeEmpty(); f.Providers.Created.ShouldBeEmpty();
        Should.Throw<IrisException>(() => new OcrProviderRegistry(f.Settings, f.SecretStore, [f.Providers, f.Providers], NullLogger<OcrProviderRegistry>.Instance));
        f.Settings.Subscriptions.ShouldBe(1); await f.Registry.StopAsync(); f.Settings.Subscriptions.ShouldBe(0); f.SecretStore.Subscriptions.ShouldBe(0);
    });

    [Fact]
    public Task Snapshot_FreezesCollectionsAndPreservesConfiguredOrderAndPrimaryFallback() => Run(async f =>
    {
        var profiles = new List<OcrProviderProfile> { Profile("b"), Profile("a"), Profile("disabled") with { Enabled = false } };
        var headers = new Dictionary<string, string> { ["x"] = "before" };
        var hotkeys = new Dictionary<IrisAction, HotkeyBinding>(); var excluded = new List<string> { "one" };
        f.Settings.Value = Config(profiles.ToArray()) with
        {
            Ocr = new() { Providers = profiles, ActiveProviderId = "b", FallbackProviderId = "a", LanguageHint = "ja" },
            Hotkeys = hotkeys,
            Translation = new() { Providers = [new() { Id = "translation", DisplayName = "t", Kind = TranslationProviderKind.OpenAICompatible, Model = "model", OpenAIOptions = new() { ExtraHeaders = headers } }] },
            Selection = new() { ExcludedProcesses = excluded }
        };
        var snapshot = await f.Acquire();
        profiles.Clear(); headers["x"] = "after"; excluded.Add("two"); hotkeys[IrisAction.InputTranslate] = new(HotkeyModifiers.Control, "A");
        snapshot.Providers.Select(p => p.Profile.Id).ShouldBe(new[] { "b", "a" }); snapshot.Settings.Ocr.Providers.Count.ShouldBe(3);
        snapshot.Settings.Ocr.ActiveProviderId.ShouldBe("b"); snapshot.Settings.Ocr.FallbackProviderId.ShouldBe("a"); snapshot.Settings.Ocr.LanguageHint.ShouldBe("ja");
        snapshot.Settings.Translation.Providers[0].OpenAIOptions!.ExtraHeaders!["x"].ShouldBe("before"); snapshot.Settings.Hotkeys.ShouldBeEmpty(); snapshot.Settings.Selection.ExcludedProcesses.Count.ShouldBe(1);
        Should.Throw<NotSupportedException>(() => ((IList<OcrProviderRegistration>)snapshot.Providers).Clear());
    });

    [Fact]
    public Task UnrelatedSettingsAndReorder_ReuseInstancesAndOnlyNotifyCollectionChanges() => Run(async f =>
    {
        f.Settings.Value = Config(Profile("a"), Profile("b")); int changes = 0;
        f.Registry.Changed += (_, _) => changes++;
        var first = await f.Acquire(); var ids = first.Providers.Select(p => p.InstanceId).ToArray();
        await f.Settings.UpdateAsync(s => s with { General = s.General with { Theme = AppTheme.Dark } }); await f.Refresh();
        var second = await f.Acquire(); second.Providers.Select(p => p.InstanceId).ShouldBe(ids); second.Settings.General.Theme.ShouldBe(AppTheme.Dark); changes.ShouldBe(1);
        await f.Settings.UpdateAsync(s => s with { Ocr = s.Ocr with { Providers = s.Ocr.Providers.Reverse().ToArray() } }); await f.Refresh();
        (await f.Acquire()).Providers.Select(p => p.InstanceId).ShouldBe(ids.Reverse()); changes.ShouldBe(2); f.Providers.Created.Count.ShouldBe(2);
    });

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    [InlineData(7)][InlineData(8)][InlineData(9)][InlineData(10)][InlineData(11)][InlineData(12)]
    public Task ProfileAndCredentialFields_RebuildWithoutInterruptingOldLease(int field) => Run(async f =>
    {
        var profile = field >= 8 ? Profile() with { BaiduOptions = new() } : Profile(); f.Settings.Value = Config(profile); var old = await f.Acquire(); var item = old.Providers.Single();
        var next = field switch
        {
            0 => profile with { DisplayName = "new" }, 1 => profile with { TimeoutSeconds = 30 },
            2 => profile with { WindowsOptions = new() { LanguageTag = "ja" } }, 3 => profile with { BaiduOptions = new() },
            4 => profile with { CredentialSetId = "bad" },
            7 => profile with { Id = "changed-id" },
            8 => profile with { BaiduOptions = new() { Mode = BaiduOcrMode.General } },
            9 => profile with { BaiduOptions = new() { DetectDirection = false } },
            10 => profile with { BaiduOptions = new() { Paragraph = false } },
            11 => profile with { BaiduOptions = new() { LanguageType = "ENG" } },
            12 => profile with { BaiduOptions = new() { BaseUrl = "https://example.test" } }, _ => profile
        };
        if (field is 5 or 6) f.SecretStore.Set(field == 5 ? SecretKeys.ApiKey(profile.Id) : SecretKeys.SecretKey(profile.Id), "changed");
        else await f.Settings.UpdateAsync(s => s with { Ocr = s.Ocr with { Providers = [next] } });
        await f.Refresh(); var current = await f.Acquire(); current.Providers.Single().InstanceId.ShouldNotBe(item.InstanceId);
        ((Provider)item.Provider).Disposed.ShouldBeFalse(); (await item.Provider.TestAsync(default)).Success.ShouldBeTrue();
        await old.DisposeAsync(); ((Provider)item.Provider).Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task DisableAndReenable_RetiresWithoutReusingLeasedInstance() => Run(async f =>
    {
        var profile = Profile(); f.Settings.Value = Config(profile);
        var old = await f.Acquire(); var original = old.Providers.Single();
        await f.Settings.UpdateAsync(_ => Config(profile with { Enabled = false })); await f.Refresh();
        (await f.Acquire()).Providers.ShouldBeEmpty();
        ((Provider)original.Provider).Disposed.ShouldBeFalse();
        await f.Settings.UpdateAsync(_ => Config(profile)); await f.Refresh();
        (await f.Acquire()).Providers.Single().InstanceId.ShouldNotBe(original.InstanceId);
        await old.DisposeAsync(); ((Provider)original.Provider).Disposed.ShouldBeTrue();
    });

    [Theory]
    [InlineData("bad", null)][InlineData("cs-00000000000000000000000000000001", null)]
    [InlineData("cs-00000000000000000000000000000001", "{\"format\":\"bad\"}")]
    public Task ExplicitBadReference_DoesNotFallBackOrInvokeLocalFactory(string set, string? json) => Run(async f =>
    {
        f.Settings.Value = Config(Profile() with { CredentialSetId = set });
        f.SecretStore.Values[SecretKeys.ApiKey("local")] = "legacy";
        if (json != null) f.SecretStore.Values[SecretKeys.CredentialSet(set)] = json;
        var snapshot = await f.Acquire(); var p = snapshot.Providers.Single().Provider;
        p.Descriptor.IsConfigured.ShouldBeFalse(); p.Constraints.MinShortSide.ShouldBe(40); p.Constraints.MaxFormUrlEncodedBytes.ShouldBeNull();
        (await p.TestAsync(default)).Error!.Code.ShouldBe(ErrorCode.NotConfigured); f.Providers.Created.ShouldBeEmpty();
        f.SecretStore.Reads.ShouldNotContain(SecretKeys.ApiKey("local"));
        Should.Throw<OperationCanceledException>(() => p.TestAsync(new CancellationToken(true)));
    });

    [Fact]
    public Task CredentialSetParsing_UsesSharedProtocolAndIgnoresUnreferencedChanges() => Run(async f =>
    {
        const string set = "cs-00000000000000000000000000000001";
        f.Settings.Value = Config(Profile() with { CredentialSetId = set });
        f.SecretStore.Values[SecretKeys.CredentialSet(set)] = "{\"format\":\"iris-provider-credentials\",\"version\":1,\"apiKey\":\"key\",\"secretKey\":\"secret\"}";
        var first = await f.Acquire(); f.Providers.Created.Single().Credentials.ShouldBe(new ProviderCredentials("key", "secret"));
        f.SecretStore.Set("unused", "new"); await f.Refresh();
        (await f.Acquire()).Providers.Single().InstanceId.ShouldBe(first.Providers.Single().InstanceId);
    });

    [Fact]
    public Task LastRetiredLease_WaitsForOneSharedAsyncDisposalAndRejectsAccessImmediately() => Run(async f =>
    {
        f.Settings.Value = Config(Profile()); var a = await f.Acquire(); var b = await f.Acquire(); var p = (Provider)a.Providers[0].Provider;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Releases.Add(() => release.TrySetResult()); p.BeforeDispose = async () => { entered.TrySetResult(); await release.Task; };
        await f.Settings.UpdateAsync(_ => Config()); await f.Refresh();
        await a.DisposeAsync(); p.DisposeCalls.ShouldBe(0);
        var end = b.DisposeAsync().AsTask(); Should.Throw<ObjectDisposedException>(() => _ = b.Providers);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); b.DisposeAsync().AsTask().ShouldBeSameAs(end);
        var retry = f.Registry.RetryCleanupAsync(); end.IsCompleted.ShouldBeFalse(); p.DisposeCalls.ShouldBe(1);
        release.TrySetResult(); await Task.WhenAll(end, retry); p.Disposed.ShouldBeTrue(); p.SyncCalls.ShouldBe(0);
    });

    [Fact]
    public Task PublishedGenerationSurvivesRetirementFailure_AndCleanupRetriesOriginalObjects() => Run(async f =>
    {
        f.Settings.Value = Config(Profile("a"), Profile("b")); await f.Refresh(); var original = f.Providers.Created.ToArray();
        foreach (var p in original) p.FailDispose = true;
        await f.Settings.UpdateAsync(_ => Config(Profile("new")));
        var error = await Should.ThrowAsync<IrisException>(() => f.Refresh(expected: typeof(IrisException))); error.Detail!.ShouldNotContain("private");
        original.ShouldAllBe(p => p.DisposeCalls > 0 && !p.Disposed);
        await Should.ThrowAsync<IrisException>(() => f.Acquire(expected: typeof(IrisException)));
        foreach (var p in original) p.FailDispose = false;
        await f.Registry.RetryCleanupAsync(); var snapshot = await f.Acquire(); snapshot.Providers.Single().Profile.Id.ShouldBe("new");
        f.Providers.Created.Count.ShouldBe(3); original.ShouldAllBe(p => p.Disposed);
    });

    [Fact]
    public Task Stop_WaitsOldSnapshotAndCancelsOnlyItsWaiter() => Run(async f =>
    {
        f.Settings.Value = Config(Profile()); var snapshot = await f.Acquire(); var p = (Provider)snapshot.Providers[0].Provider;
        using var caller = new CancellationTokenSource(); var stop = f.Registry.StopAsync(caller.Token); caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => stop); p.Disposed.ShouldBeFalse(); snapshot.Providers.Count.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => f.Registry.RefreshAsync());
        var joined = f.Registry.StopAsync(); joined.IsCompleted.ShouldBeFalse(); await snapshot.DisposeAsync(); await joined;
        p.Disposed.ShouldBeTrue(); f.Registry.Dispose();
    });

    [Fact]
    public Task TransientDisabledDraft_UsesIndependentLeaseAndClearsOnRelease() => Run(async f =>
    {
        var transient = await f.Transient(Profile() with { Enabled = false }, new("draft", "secret"));
        var p = (Provider)transient.Providers[0].Provider; p.Credentials.ApiKey.ShouldBe("draft");
        await transient.DisposeAsync(); p.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task Di_RegistersOneRegistryAndOneOcrObserver() => Run(async f =>
    {
        var services = new ServiceCollection(); services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance); services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>)); services.AddSingleton<ISettingsService>(f.Settings); services.AddSingleton<ISecretStore>(f.SecretStore); services.AddSingleton<IOcrProviderFactory>(f.Providers);
        services.AddIrisCore(); services.AddIrisCore();
        await using var sp = services.BuildServiceProvider(); var registry = sp.GetRequiredService<IOcrProviderRegistry>();
        registry.ShouldBeSameAs(sp.GetRequiredService<OcrProviderRegistry>());
        sp.GetServices<IProviderConfigurationObserver>().OfType<OcrProviderConfigurationObserver>().Count().ShouldBe(1);
        await registry.StopAsync();
    });

    [Fact]
    public Task CompletedRetirement_IsNotCachedOnLiveRegistry() => Run(async f =>
    {
        var weak = await Retire(f); f.Providers.Created.Clear(); f.Work.Clear(); await Task.Yield();
        for (int i = 0; i < 5 && weak.Any(w => w.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        weak.ShouldAllBe(w => !w.IsAlive); GC.KeepAlive(f.Registry);
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> Retire(OcrRegistryTestScope f)
    {
        f.Settings.Value = Config(Profile()); var snapshot = await f.Acquire(); var provider = snapshot.Providers[0].Provider;
        await f.Settings.UpdateAsync(_ => Config()); await f.Refresh(); await snapshot.DisposeAsync();
        return [new(provider), new(new byte[1024])];
    }
}
