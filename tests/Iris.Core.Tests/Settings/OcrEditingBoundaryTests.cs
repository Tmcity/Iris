using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Shouldly;
using static Iris.Core.Tests.Settings.OcrEditingTestScope;

namespace Iris.Core.Tests.Settings;

public sealed class OcrEditingBoundaryTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public Task ScalarConflict_RechecksAtCommitAndRejectsWholeTransaction(bool ocr) => Run(async f =>
    {
        var plan = Plan() with { General = new(Theme: new(AppTheme.System, AppTheme.Dark)) };
        if (ocr) plan = plan with { Ocr = new(LanguageHint: new("auto", "en")) };
        else plan = plan with { Screenshot = new(MaskOpacity: new(f.Settings.Value.Screenshot.MaskOpacity, 0.8)) };
        f.Settings.BeforeMutate = () => f.Settings.Value = ocr
            ? f.Settings.Value with { Ocr = f.Settings.Value.Ocr with { LanguageHint = "ja" } }
            : f.Settings.Value with { Screenshot = f.Settings.Value.Screenshot with { MaskOpacity = 0.7 } };
        var result = await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.IsConflict.ShouldBeTrue();
        f.Settings.Value.General.Theme.ShouldBe(AppTheme.System); f.Settings.Updates.ShouldBe(0);
    });

    [Fact]
    public Task ScalarsAlreadyAtTargetAndLanguageHintNormalization_AreNoOp() => Run(async f =>
    {
        var initial = f.Settings.Value;
        var plan = Plan() with { Ocr = new(LanguageHint: new("old", " \t ")), Screenshot = new(MaskOpacity: new(0.1, initial.Screenshot.MaskOpacity)) };
        (await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.ShouldBeSameAs(initial); f.SecretStore.Writes.ShouldBeEmpty();
        f.OtherObserver.Calls.ShouldBe(1); f.OcrObserver.Calls.ShouldBe(1);
    });

    [Fact]
    public Task DeleteSelectedCustomProvider_WithMatchingSelectionPatchPreservesRemainingOrder() => Run(async f =>
    {
        var a = Profile("a"); var b = Profile("b"); var c = Profile("c");
        f.Settings.Value = new() { Ocr = new() { Providers = [a, b, c], ActiveProviderId = "b", FallbackProviderId = "a" } };
        var plan = Plan(new OcrProfileChange(OcrProfileChangeKind.Delete, b, null, new(CredentialEditKind.Keep))) with
        { Ocr = new(ActiveProviderId: new("b", "c"), FallbackProviderId: new("a", null)) };
        (await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.Ocr.Providers.Select(p => p.Id).ShouldBe(["a", "c"]);
        f.Settings.Value.Ocr.ActiveProviderId.ShouldBe("c"); f.Settings.Value.Ocr.FallbackProviderId.ShouldBeNull();
    });

    [Fact]
    public Task Cleanup_RechecksCrossDomainReferencesBeforeEachCandidate() => Run(async f =>
    {
        const string first = "cs-00000000000000000000000000000001", second = "cs-00000000000000000000000000000002";
        var a = Profile("a", OcrProviderKind.BaiduCloud) with { CredentialSetId = first };
        var b = Profile("b", OcrProviderKind.BaiduCloud) with { CredentialSetId = second };
        f.Settings.Value = new() { Ocr = new() { Providers = [a, b], ActiveProviderId = "a", FallbackProviderId = "b" } };
        f.SecretStore.Values[SecretKeys.CredentialSet(first)] = "old one"; f.SecretStore.Values[SecretKeys.CredentialSet(second)] = "old two";
        f.SecretStore.BeforeRemove = key =>
        {
            if (key != SecretKeys.CredentialSet(first)) return;
            f.Settings.Value = f.Settings.Value with { Translation = new() { Providers = [new()
            { Id = "shared", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "shared", Model = "", Enabled = false, CredentialSetId = second }] } };
        };
        var plan = Plan(Update(a, a, new(CredentialEditKind.Clear)), Update(b, b, new(CredentialEditKind.Clear)));
        (await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.SecretStore.Values.ShouldNotContainKey(SecretKeys.CredentialSet(first));
        f.SecretStore.Values.ShouldContainKey(SecretKeys.CredentialSet(second)); f.SecretStore.Removes.ShouldNotContain(SecretKeys.CredentialSet(second));
    });

    [Fact]
    public Task CancellationDuringStartedSecretWrite_WaitsThenRollsBack() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.NewGate();
        f.SecretStore.BeforeSet = _ => gate.Pause(); var b = f.Settings.Value.Ocr.Providers[0];
        var work = f.Track(f.Coordinator.CommitAsync(Plan(Update(b, b, new(CredentialEditKind.Replace, new("fake", "pair")))), caller.Token), typeof(OperationCanceledException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        caller.Cancel(); work.IsCompleted.ShouldBeFalse(); f.Settings.Updates.ShouldBe(0);
        gate.Open(); await Should.ThrowAsync<OperationCanceledException>(() => work); f.SecretStore.Values.ShouldBeEmpty();
    });

    [Fact]
    public Task DraftConstruction_DoesNotExposeSecretsInNewDtoStrings() => Run(async f =>
    {
        const string marker = "private-draft-marker";
        var profile = Profile(marker, OcrProviderKind.BaiduCloud) with { BaiduOptions = new() { LanguageType = marker } };
        var draft = new OcrProviderDraft(profile, null, new(CredentialEditKind.Replace, new(marker, marker)));
        var change = new OcrProfileChange(OcrProfileChangeKind.Add, null, profile, draft.Credentials);
        object[] objects = [draft, change, Plan(change), new OcrSettingsPatch(LanguageHint: new(marker, marker)), new ScreenshotSettingsPatch()];
        foreach (var value in objects) value.ToString()!.ShouldNotContain(marker);
        var session = f.Draft(draft); await session.DisposeAsync(); f.Settings.Reads.ShouldBe(0); f.SecretStore.Reads.ShouldBeEmpty();
    });

    [Fact]
    public Task ProviderFailureAfterCancellation_IsStillObservableAndSafe() => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var gate = f.NewGate();
        f.Local.Configure = p => p.Test = async _ => { await gate.PauseAsync(); throw new IrisException(ErrorCode.StorageFailure, "private cleanup failure"); };
        var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
        var work = f.Track(session.RunAsync(caller.Token));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); caller.Cancel(); gate.Open();
        var result = await work; result.Error!.Code.ShouldBe(ErrorCode.StorageFailure); result.Error.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.StorageFailure));
        await session.DisposeAsync(); f.Local.Created.Single().Disposed.ShouldBeTrue();
    });

    [Fact]
    public async Task InjectedAssertionFailure_ReleasesGateJoinsActualTaskAndDisposesSession()
    {
        OcrEditingTestScope.Provider? provider = null; Task? work = null;
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Run(async f =>
        {
            var gate = f.NewGate(); f.Local.Configure = p => { provider = p; p.Test = async _ => { await gate.PauseAsync(); return new(true, TimeSpan.Zero); }; };
            var session = f.Draft(new(Profile(), null, new(CredentialEditKind.Keep)));
            work = f.Track(session.RunAsync(TestContext.Current.CancellationToken));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            throw new InvalidOperationException("injected assertion marker");
        }));
        failure.Message.ShouldBe("injected assertion marker"); work!.IsCompleted.ShouldBeTrue(); provider!.Disposed.ShouldBeTrue();
    }
}
