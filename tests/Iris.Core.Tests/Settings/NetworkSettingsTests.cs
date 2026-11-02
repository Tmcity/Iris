using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using static Iris.Core.Tests.Settings.OcrEditingTestScope;

namespace Iris.Core.Tests.Settings;

public sealed class NetworkSettingsTests
{
    private static readonly Uri Remote = new("https://origin.example.test/path?private=query");
    private static readonly Uri Proxy = new("http://proxy.example.test:8080/");
    private const string OldId = "pc-00000000000000000000000000000001";
    private static SettingsEditPlan Plan(NetworkSettingsPatch patch) => new(new(), []) { Network = patch };
    private static SettingsEditCoordinator Coordinator(OcrEditingTestScope f, Observer observer) => new(f.Settings, f.SecretStore,
        Substitute.For<ITranslationProviderRegistry>(), [f.OtherObserver], Substitute.For<IPromptTemplateProvider>(), TimeProvider.System,
        NullLogger<SettingsEditCoordinator>.Instance, ocrProviderRegistry: f.Registry, networkObservers: [observer]);
    private static NetworkSettings Seed(OcrEditingTestScope f)
    {
        var network = new NetworkSettings { ProxyMode = ProxyMode.Custom, CustomProxyUrl = Proxy.AbsoluteUri, ProxyCredentialSetId = OldId };
        f.Settings.Value = f.Settings.Value with { Network = network };
        f.SecretStore.Set(SecretKeys.ProxyCredentialSet(OldId), NetworkProxyCodec.Serialize(Proxy, new("old-user", "old-password")));
        return network;
    }
    private static NetworkSettingsPatch Replace(NetworkSettings baseline, string password = "new-password") => new(
        Credentials: new(CredentialEditKind.Replace, baseline.CustomProxyUrl, baseline.ProxyCredentialSetId, new("new-user", password)));

    [Theory]
    [InlineData("http://Proxy.Example.Test", "http://proxy.example.test/")]
    [InlineData("https://Proxy.Example.Test:443/", "https://proxy.example.test/")]
    [InlineData("socks5://proxy.example.test", "socks5://proxy.example.test:1080/")]
    public void Codec_NormalizesApprovedUris(string input, string expected) => NetworkProxyCodec.ParseUri(input).AbsoluteUri.ShouldBe(expected);

    [Theory]
    [InlineData("http://u:p@proxy.example.test/")][InlineData("http://proxy.example.test/path")]
    [InlineData("http://proxy.example.test/?key=secret")][InlineData("http://proxy.example.test/#private")]
    [InlineData("ftp://proxy.example.test")][InlineData("socks4://proxy.example.test")]
    [InlineData("http://proxy.example.test:0")][InlineData(" http://proxy.example.test")]
    [InlineData("http://proxy.example.test\r\n")]
    public void Codec_RejectsUnsafeInputWithoutEcho(string input)
    {
        var ex = Should.Throw<IrisException>(() => NetworkProxyCodec.ParseUri(input));
        ex.Code.ShouldBe(ErrorCode.InvalidArgument); ex.ToString().ShouldNotContain(input); ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Credentials_BudgetsNullPairsAndToStringAreStrict()
    {
        NetworkProxyCodec.ValidateCredentials(new(new string('u', 1024), new string('p', 4096)), Proxy);
        foreach (var pair in new[] { new ProxyCredentials(new string('u', 1025), "p"), new("u", new string('p', 4097)), new("u\0", "p"), new("u", "\ud800"), new(null!, "p") })
            Should.Throw<IrisException>(() => NetworkProxyCodec.ValidateCredentials(pair, Proxy));
        var socks = NetworkProxyCodec.ParseUri("socks5://proxy.example.test");
        Should.Throw<IrisException>(() => NetworkProxyCodec.ValidateCredentials(new("", "p"), socks));
        Should.Throw<IrisException>(() => NetworkProxyCodec.ValidateCredentials(new(new string('中', 86), "p"), socks));
        var empty = NetworkProxyCodec.Serialize(Proxy, null);
        empty.ShouldContain("\"userName\":null"); empty.ShouldContain("\"password\":null");
        NetworkProxyCodec.Deserialize(empty, Proxy).ShouldBeNull();
        Should.Throw<IrisException>(() => NetworkProxyCodec.Deserialize(empty, new("http://different.example.test/")));
        new ProxyCredentials("private", "secret").ToString().ShouldBe("ProxyCredentials");
        new NetworkSettings { CustomProxyUrl = "private", ProxyCredentialSetId = "secret" }.ToString().ShouldBe("NetworkSettings");
        new NetworkProxySnapshot(Guid.NewGuid(), Remote, ProxyMode.Custom, Proxy, new("private", "secret"), ProxyRouteKind.Proxy).ToString().ShouldBe("NetworkProxySnapshot");
    }

    [Fact]
    public Task Replace_StageOnceSingleUpdateNetworkOnlyBarrierAndRetirement() => Run(async f =>
    {
        var baseline = Seed(f); var observer = new Observer(); using var c = Coordinator(f, observer);
        f.OtherObserver.Fail = true;
        var oldSnapshot = await c.CaptureCurrentAsync(Remote);
        var result = await c.CommitAsync(Plan(Replace(baseline)));
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Settings.Updates.ShouldBe(1); f.OtherObserver.Calls.ShouldBe(0);
        f.Settings.Value.Network.ProxyCredentialSetId.ShouldNotBe(OldId);
        f.SecretStore.Values.ShouldNotContainKey(SecretKeys.ProxyCredentialSet(OldId));
        oldSnapshot.Credentials!.Password.ShouldBe("old-password");
        var latest = await c.CaptureCurrentAsync(Remote);
        latest.Credentials!.Password.ShouldBe("new-password"); latest.GenerationId.ShouldNotBe(oldSnapshot.GenerationId);
        latest.GenerationId.ShouldBe(observer.Generations.Last());
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task FieldAndCredentialOwnershipConflicts_AreRecheckedInsideUpdate(bool inside) => Run(async f =>
    {
        var baseline = Seed(f); using var c = Coordinator(f, new());
        void Change() => f.Settings.Value = f.Settings.Value with { Network = baseline with { ProxyCredentialSetId = "pc-00000000000000000000000000000002" } };
        if (inside) f.Settings.BeforeMutate = Change; else Change();
        var result = await c.CommitAsync(Plan(Replace(baseline)));
        result.IsConflict.ShouldBeTrue(); f.Settings.Updates.ShouldBe(0);
        f.SecretStore.Values.Keys.Where(k => k.StartsWith("network/", StringComparison.Ordinal)).ShouldBe([SecretKeys.ProxyCredentialSet(OldId)]);
    });

    [Fact]
    public Task StageAndUpdateFailure_CancelRollbackAndOriginalReceiptNeverRestage() => Run(async f =>
    {
        var baseline = Seed(f); var observer = new Observer(); using var c = Coordinator(f, observer);
        f.SecretStore.BeforeSet = _ => throw new IOException("synthetic-secret");
        (await c.CommitAsync(Plan(Replace(baseline)))).Status.ShouldBe(SettingsEditStatus.Uncommitted);
        f.SecretStore.BeforeSet = null; f.Settings.BeforeMutate = () => throw new IOException("synthetic-secret");
        (await c.CommitAsync(Plan(Replace(baseline)))).Status.ShouldBe(SettingsEditStatus.Uncommitted);
        f.SecretStore.Values.Count.ShouldBe(1); f.Settings.BeforeMutate = null;
        using var cancel = new CancellationTokenSource(); f.SecretStore.BeforeSet = _ => cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => c.CommitAsync(Plan(Replace(baseline)), cancel.Token));
        f.SecretStore.BeforeSet = null; f.SecretStore.Values.Count.ShouldBe(1);
        observer.Fail = true;
        var pending = await c.CommitAsync(Plan(Replace(baseline)));
        pending.Status.ShouldBe(SettingsEditStatus.ApplyPending); pending.PendingApply.ShouldNotBeNull();
        pending.Error!.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        f.SecretStore.Values.ShouldContainKey(SecretKeys.ProxyCredentialSet(OldId));
        int writes = f.SecretStore.Writes.Count;
        observer.Fail = false; f.SecretStore.BeforeRemove = _ => throw new IOException("private");
        (await c.RetryApplyAsync(pending.PendingApply!)).IsComplete.ShouldBeFalse();
        f.SecretStore.BeforeRemove = null;
        // A newer current reference wins over an old receipt's cleanup candidate.
        f.Settings.Value = f.Settings.Value with { Network = baseline };
        (await c.RetryApplyAsync(pending.PendingApply!)).IsComplete.ShouldBeTrue();
        f.SecretStore.Values.ShouldContainKey(SecretKeys.ProxyCredentialSet(OldId));
        f.SecretStore.Writes.Count.ShouldBe(writes); f.Settings.Updates.ShouldBe(1);
    });

    [Fact]
    public Task MixedNetworkOcrTranslationAndHistory_OneCommitBothBarriers() => Run(async f =>
    {
        var baseline = Seed(f); var observer = new Observer(); using var c = Coordinator(f, observer);
        var p = new TranslationProviderProfile { Id = "shared", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "old", BaseUrl = "https://origin.example.test/v1", Model = "synthetic" };
        f.Settings.Value = f.Settings.Value with { Translation = f.Settings.Value.Translation with { Providers = [p] } };
        var ocr = f.Settings.Value.Ocr.Providers[0];
        f.OtherObserver.Fail = true;
        var plan = new SettingsEditPlan(new(LogLevel: new("Information", "Warning")),
            [new(TranslationProfileChangeKind.Update, p, p with { DisplayName = "updated" }, new(CredentialEditKind.Replace, new("translation", null)))])
        {
            Network = Replace(baseline), History = new(MaxEntries: new(f.Settings.Value.History.MaxEntries, 5432)),
            OcrChanges = [new(OcrProfileChangeKind.Update, ocr, ocr with { DisplayName = "updated" }, new(CredentialEditKind.Clear))]
        };
        var result = await c.CommitAsync(plan);
        result.Status.ShouldBe(SettingsEditStatus.ApplyPending); f.Settings.Updates.ShouldBe(1);
        f.Settings.Value.History.MaxEntries.ShouldBe(5432); observer.Generations.Count.ShouldBe(1);
        f.OtherObserver.Fail = false;
        (await c.RetryApplyAsync(result.PendingApply!)).IsComplete.ShouldBeTrue();
        f.Settings.Updates.ShouldBe(1); f.Settings.Value.Ocr.Providers[0].CredentialSetId.ShouldStartWith("cs-");
        f.Settings.Value.Network.ProxyCredentialSetId.ShouldStartWith("pc-");
    });

    [Fact]
    public Task CaptureAndStage_ShareGateAndCancellationJoinsSynchronousGet() => Run(async f =>
    {
        Seed(f); using var c = Coordinator(f, new()); var gate = f.NewGate();
        f.SecretStore.BeforeGet = _ => gate.Pause();
        using var cancellation = new CancellationTokenSource();
        var capture = f.Track(c.CaptureCurrentAsync(Remote, cancellation.Token), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel(); capture.IsCompleted.ShouldBeFalse();
        var commit = f.Track(c.CommitAsync(Plan(Replace(f.Settings.Value.Network))));
        commit.IsCompleted.ShouldBeFalse(); f.Settings.Updates.ShouldBe(0);
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => capture); await commit;
        f.Settings.Updates.ShouldBe(1);
    });

    [Fact]
    public Task CurrentKeyMutationRotatesGeneration_DraftDoesNotPublishAndClearDoesNotReviveLegacy() => Run(async f =>
    {
        var baseline = Seed(f); var observer = new Observer(); using var c = Coordinator(f, observer);
        var first = await c.CaptureCurrentAsync(Remote);
        f.SecretStore.Set(SecretKeys.ProxyCredentialSet(OldId), NetworkProxyCodec.Serialize(Proxy, new("same", "rotated")));
        var second = await c.CaptureCurrentAsync(Remote); second.GenerationId.ShouldNotBe(first.GenerationId);
        int published = observer.Generations.Count;
        var draft = await c.CaptureDraftAsync(new(baseline, baseline, Replace(baseline).Credentials), Remote);
        draft.Credentials!.Password.ShouldBe("new-password"); observer.Generations.Count.ShouldBe(published);
        (await c.CaptureCurrentAsync(Remote)).GenerationId.ShouldBe(second.GenerationId);
        f.SecretStore.Set(SecretKeys.LegacyProxyCredentials, "unrelated-private");
        var result = await c.CommitAsync(Plan(new(Credentials: new(CredentialEditKind.Clear, baseline.CustomProxyUrl, OldId))));
        result.Status.ShouldBe(SettingsEditStatus.Applied);
        (await c.CaptureCurrentAsync(Remote)).Credentials.ShouldBeNull();
        f.SecretStore.Reads.ShouldNotContain(SecretKeys.LegacyProxyCredentials);
    });

    [Theory][InlineData(ProxyMode.None)][InlineData(ProxyMode.System)][InlineData(ProxyMode.Custom)]
    public Task LocalBypassDoesNotReadBadDormantCredentials(ProxyMode mode) => Run(async f =>
    {
        f.Settings.Value = f.Settings.Value with { Network = new() { ProxyMode = mode, CustomProxyUrl = "invalid", ProxyCredentialSetId = "../bad" } };
        using var c = Coordinator(f, new());
        (await c.CaptureCurrentAsync(new("http://localhost/"))).Mode.ShouldBe(ProxyMode.None);
        f.SecretStore.Reads.ShouldBeEmpty();
        if (mode == ProxyMode.Custom) await Should.ThrowAsync<IrisException>(() => c.CaptureCurrentAsync(Remote));
    });

    [Fact]
    public Task UrlChangeRequiresExplicitCredentials_AndUneditedDormantFieldsFollowCurrent() => Run(async f =>
    {
        var baseline = Seed(f); using var c = Coordinator(f, new());
        var invalid = await c.CommitAsync(Plan(new(CustomProxyUrl: new(baseline.CustomProxyUrl, "http://new.example.test/"))));
        invalid.Error!.Code.ShouldBe(ErrorCode.InvalidArgument); f.Settings.Updates.ShouldBe(0);
        f.Settings.BeforeMutate = () => f.Settings.Value = f.Settings.Value with { Network = baseline with { CustomProxyUrl = "dormant-invalid", ProxyCredentialSetId = "dormant-invalid" } };
        (await c.CommitAsync(Plan(new(ProxyMode: new(ProxyMode.Custom, ProxyMode.None))))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.Network.CustomProxyUrl.ShouldBe("dormant-invalid"); f.Settings.Value.Network.ProxyCredentialSetId.ShouldBe("dormant-invalid");
    });

    [Fact]
    public Task CustomKeepRequiresUsableCredentialReference_WhileNonePreservesDormantIntent() => Run(async f =>
    {
        var baseline = Seed(f); using var c = Coordinator(f, new());
        f.SecretStore.Values[SecretKeys.ProxyCredentialSet(OldId)] = "corrupt-private-value";
        int writes = f.SecretStore.Writes.Count;
        var invalid = await c.CommitAsync(Plan(new(BypassProxyOnLocal: new(true, false))));
        invalid.Error!.Code.ShouldBe(ErrorCode.SecretUnavailable); f.Settings.Updates.ShouldBe(0); f.SecretStore.Writes.Count.ShouldBe(writes);
        (await c.CommitAsync(Plan(new(ProxyMode: new(ProxyMode.Custom, ProxyMode.None))))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.Network.ProxyCredentialSetId.ShouldBe(baseline.ProxyCredentialSetId);
    });

    [Fact]
    public Task InPlaceValueWithoutEventIsDetected_AndContinuouslyChangingStoreIsBounded() => Run(async f =>
    {
        Seed(f); using var c = Coordinator(f, new());
        var first = await c.CaptureCurrentAsync(Remote);
        f.SecretStore.Values[SecretKeys.ProxyCredentialSet(OldId)] = NetworkProxyCodec.Serialize(Proxy, new("replacement", "replacement"));
        (await c.CaptureCurrentAsync(Remote)).GenerationId.ShouldNotBe(first.GenerationId);
        string proxyKey = SecretKeys.ProxyCredentialSet(OldId);
        int reads = f.SecretStore.Reads.Count(key => key == proxyKey), changes = 0;
        f.SecretStore.BeforeGet = key =>
        {
            if (key == proxyKey)
                f.SecretStore.Values[key] = NetworkProxyCodec.Serialize(Proxy, new("changing", Interlocked.Increment(ref changes).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        };
        var error = await Should.ThrowAsync<IrisException>(() => c.CaptureCurrentAsync(Remote)); error.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        (f.SecretStore.Reads.Count(key => key == proxyKey) - reads).ShouldBe(8);
    });

    [Fact]
    public Task ContinuouslyChangingProxyReadBudget_IgnoresConcurrentOcrReads() => Run(async f =>
    {
        Seed(f); using var c = Coordinator(f, new());
        string proxyKey = SecretKeys.ProxyCredentialSet(OldId);
        const string otherKey = "ocr/read-budget-regression/apiKey";
        f.SecretStore.Values[otherKey] = "unchanged-synthetic-value";
        int reads = f.SecretStore.Reads.Count(key => key == proxyKey);
        int otherReads = f.SecretStore.Reads.Count(key => key == otherKey);
        int totalReads = f.SecretStore.Reads.Count, changes = 0;
        var gate = f.NewGate();
        f.SecretStore.BeforeGet = key =>
        {
            if (key != proxyKey) return;
            int change = Interlocked.Increment(ref changes);
            if (change == 1) gate.Pause();
            f.SecretStore.Values[key] = NetworkProxyCodec.Serialize(Proxy, new("changing", change.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        };
        var capture = f.Track(c.CaptureCurrentAsync(Remote), typeof(IrisException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var otherWork = f.Track(Task.Run(() =>
        {
            for (int index = 0; index < 4; index++) f.SecretStore.Get(otherKey);
        }));
        await otherWork;
        gate.Open();
        var error = await Should.ThrowAsync<IrisException>(() => capture);
        error.Code.ShouldBe(ErrorCode.NetworkUnavailable);
        (f.SecretStore.Reads.Count(key => key == proxyKey) - reads).ShouldBe(8);
        (f.SecretStore.Reads.Count(key => key == otherKey) - otherReads).ShouldBe(4);
        (f.SecretStore.Reads.Count - totalReads).ShouldBeGreaterThanOrEqualTo(12);
        f.SecretStore.Values[otherKey].ShouldBe("unchanged-synthetic-value");
    });

    private sealed class Observer : INetworkConfigurationObserver
    {
        internal bool Fail;
        internal readonly List<Guid> Generations = [];
        public Task ApplyAsync(NetworkSettings settings, Guid generationId, CancellationToken cancellationToken = default)
        {
            Generations.Add(generationId);
            if (Fail) throw new IOException("synthetic private observer failure");
            return Task.CompletedTask;
        }
    }
}
