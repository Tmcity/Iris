using System.Text.Json;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Shouldly;
using static Iris.Core.Tests.Settings.OcrEditingTestScope;

namespace Iris.Core.Tests.Settings;

public sealed class OcrSettingsEditingTests
{
    [Fact]
    public Task JointPlan_PublishesAllDomainsOnceAndKeepsUneditedLatestValues() => Run(async f =>
    {
        var b = f.Settings.Value.Ocr.Providers[0]; var edited = b with { DisplayName = "edited", Enabled = false, TimeoutSeconds = 120 };
        var added = Profile("added");
        var plan = Plan(Update(b, edited), new(OcrProfileChangeKind.Add, null, added, new(CredentialEditKind.Keep))) with
        {
            General = new(Theme: new(AppTheme.System, AppTheme.Dark)),
            Ocr = new(ActiveProviderId: new(b.Id, added.Id), FallbackProviderId: new(f.Settings.Value.Ocr.FallbackProviderId, b.Id), TextLayout: new(f.Settings.Value.Ocr.TextLayout, OcrTextLayout.KeepLines),
                LanguageHint: new("auto", " ja "), AutoCopyOnOcr: new(false, true), SilentOcrShowNotification: new(true, false)),
            Screenshot = new(new(true, false), new(true, false), new(true, false), new(false, true), new(f.Settings.Value.Screenshot.MaskOpacity, 0.9)),
            Selection = new(OpenInputWhenEmpty: new(f.Settings.Value.Selection.OpenInputWhenEmpty, false)),
            HotkeyChanges = [new(IrisAction.SilentScreenshotOcr, f.Settings.Value.Hotkeys[IrisAction.SilentScreenshotOcr], new(HotkeyModifiers.Alt | HotkeyModifiers.Control, "P"))]
        };
        f.Settings.BeforeMutate = () => f.Settings.Value = f.Settings.Value with
        { General = f.Settings.Value.General with { UiLanguage = "en-US" }, TranslateWindow = new() { Width = 777 } };
        var result = await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Settings.Updates.ShouldBe(1);
        var s = f.Settings.Value; s.General.Theme.ShouldBe(AppTheme.Dark); s.General.UiLanguage.ShouldBe("en-US"); s.TranslateWindow.Width.ShouldBe(777);
        s.Ocr.Providers.Select(p => p.Id).ShouldBe([b.Id, BuiltInProviderIds.WindowsOcr, "added"]);
        s.Ocr.Providers[0].ShouldBe(edited); s.Ocr.LanguageHint.ShouldBe("ja"); s.Ocr.ActiveProviderId.ShouldBe("added"); s.Ocr.FallbackProviderId.ShouldBe(b.Id);
        s.Ocr.AutoCopyOnOcr.ShouldBeTrue(); s.Ocr.SilentOcrShowNotification.ShouldBeFalse(); s.Ocr.TextLayout.ShouldBe(OcrTextLayout.KeepLines);
        s.Screenshot.ShouldBe(new ScreenshotSettings { ConfirmOnMouseUp = false, ShowMagnifier = false, HideIrisWindows = false, CopyImageToClipboard = true, MaskOpacity = 0.9 });
        f.OtherObserver.Calls.ShouldBe(1); f.OcrObserver.Calls.ShouldBe(1);
    });

    [Fact]
    public Task SameIdAcrossDomains_StagesAndAppliesDistinctCredentialPairs() => Run(async f =>
    {
        var ocr = Profile("same", OcrProviderKind.BaiduCloud);
        var translation = new TranslationProviderProfile { Id = "same", Kind = TranslationProviderKind.OpenAICompatible, Model = "", DisplayName = "translation", Enabled = false };
        f.Settings.Value = new() { Translation = new() { Providers = [translation] }, Ocr = new() { Providers = [ocr], ActiveProviderId = "same", FallbackProviderId = null } };
        var plan = Plan(Update(ocr, ocr, new(CredentialEditKind.Replace, new("ocr-key", "ocr-secret")))) with
        { TranslationChanges = [new(TranslationProfileChangeKind.Update, translation, translation, new(CredentialEditKind.Replace, new("translation-key", "translation-secret")))] };
        (await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        string a = f.Settings.Value.Ocr.Providers.Single().CredentialSetId!, b = f.Settings.Value.Translation.Providers.Single().CredentialSetId!;
        a.ShouldNotBe(b); f.SecretStore.Writes.Count.ShouldBe(2); f.Settings.Updates.ShouldBe(1);
        using var oa = JsonDocument.Parse(f.SecretStore.Values[SecretKeys.CredentialSet(a)]);
        using var tb = JsonDocument.Parse(f.SecretStore.Values[SecretKeys.CredentialSet(b)]);
        oa.RootElement.GetProperty("apiKey").GetString().ShouldBe("ocr-key"); oa.RootElement.GetProperty("secretKey").GetString().ShouldBe("ocr-secret");
        tb.RootElement.GetProperty("apiKey").GetString().ShouldBe("translation-key");
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task UpdateConflict_BeforeOrInsideCommitRejectsWholePlanAndRollsBackStage(bool inside) => Run(async f =>
    {
        var profile = f.Settings.Value.Ocr.Providers[0];
        var plan = Plan(Update(profile, profile with { DisplayName = "target" }, new(CredentialEditKind.Replace, new("new", "pair")))) with
        { Screenshot = new(MaskOpacity: new(f.Settings.Value.Screenshot.MaskOpacity, 0.9)) };
        void Interfere() => f.Settings.Value = f.Settings.Value with { Ocr = f.Settings.Value.Ocr with
        { Providers = [profile with { BaiduOptions = profile.BaiduOptions! with { DetectDirection = !profile.BaiduOptions!.DetectDirection } }, f.Settings.Value.Ocr.Providers[1]] } };
        if (inside) f.Settings.BeforeMutate = Interfere; else Interfere();
        var result = await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.IsConflict.ShouldBeTrue(); f.Settings.Updates.ShouldBe(0);
        f.Settings.Value.Screenshot.MaskOpacity.ShouldNotBe(0.9); f.SecretStore.Values.ShouldBeEmpty();
        f.SecretStore.Writes.Count.ShouldBe(inside ? 1 : 0);
    });

    [Fact]
    public Task EntryFreeze_DetachesCallerListAndPreservesOtherProfileUpdatedDuringAwait() => Run(async f =>
    {
        var b = f.Settings.Value.Ocr.Providers[0]; var other = f.Settings.Value.Ocr.Providers[1];
        var changes = new List<OcrProfileChange> { Update(b, b with { DisplayName = "captured" }) };
        var gate = f.NewGate(); f.Settings.BeforeUpdate = gate.PauseAsync;
        var task = f.Track(f.Coordinator.CommitAsync(Plan() with { OcrChanges = changes }, TestContext.Current.CancellationToken));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        changes[0] = new(OcrProfileChangeKind.Delete, b, null, new(CredentialEditKind.Keep));
        f.Settings.Value = f.Settings.Value with { Ocr = f.Settings.Value.Ocr with { Providers = [b, other with { DisplayName = "latest other" }] } };
        gate.Open(); (await task).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.Ocr.Providers.Select(p => p.DisplayName).ShouldBe(["captured", "latest other"]);
    });

    [Theory][InlineData("active-missing")][InlineData("fallback-same")][InlineData("fallback-missing")][InlineData("bad-layout")]
    [InlineData("null-active")][InlineData("delete-selected")][InlineData("delete-built-in")][InlineData("duplicate")]
    [InlineData("kind-change")][InlineData("reference-change")][InlineData("incomplete-pair")][InlineData("local-replace")]
    [InlineData("bad-url")][InlineData("bad-mode")][InlineData("null-item")][InlineData("null-list")]
    public Task InvalidTargets_AreRejectedBeforeStagingOrSettings(string scenario) => Run(async f =>
    {
        var profile = Profile("custom", OcrProviderKind.BaiduCloud);
        f.Settings.Value = new() { Ocr = new() { Providers = [profile], ActiveProviderId = profile.Id, FallbackProviderId = null } };
        var plan = scenario switch
        {
            "active-missing" => Plan() with { Ocr = new(ActiveProviderId: new("custom", "missing")) },
            "null-active" => Plan() with { Ocr = new(ActiveProviderId: new("custom", null!)) },
            "fallback-same" => Plan() with { Ocr = new(FallbackProviderId: new(null, "custom")) },
            "fallback-missing" => Plan() with { Ocr = new(FallbackProviderId: new(null, "missing")) },
            "bad-layout" => Plan() with { Ocr = new(TextLayout: new(OcrTextLayout.KeepLines, (OcrTextLayout)99)) },
            "delete-selected" => Plan(new OcrProfileChange(OcrProfileChangeKind.Delete, profile, null, new(CredentialEditKind.Keep))),
            "delete-built-in" => Plan(new OcrProfileChange(OcrProfileChangeKind.Delete, Profile(BuiltInProviderIds.BaiduOcr), null, new(CredentialEditKind.Keep))),
            "duplicate" => Plan(Update(profile, profile), Update(profile, profile)),
            "kind-change" => Plan(Update(profile, profile with { Kind = OcrProviderKind.WindowsLocal })),
            "reference-change" => Plan(Update(profile, profile with { CredentialSetId = "cs-00000000000000000000000000000000" })),
            "incomplete-pair" => Plan(Update(profile, profile, new(CredentialEditKind.Replace, new("only-key")))),
            "local-replace" => Plan(new OcrProfileChange(OcrProfileChangeKind.Add, null, Profile("new-local"), new(CredentialEditKind.Replace, new("not-allowed", "secret")))),
            "bad-url" => Plan(Update(profile, profile with { BaiduOptions = new() { BaseUrl = "https://user:pass@example.test?query=private" } })),
            "bad-mode" => Plan(Update(profile, profile with { BaiduOptions = new() { Mode = (BaiduOcrMode)99 } })),
            "null-item" => Plan(null!),
            _ => Plan() with { OcrChanges = null! }
        };
        var result = await f.Coordinator.CommitAsync(plan, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Settings.Updates.ShouldBe(0); f.SecretStore.Writes.ShouldBeEmpty();
    });

    [Theory][InlineData(4, false)][InlineData(5, true)][InlineData(120, true)][InlineData(121, false)]
    public Task TimeoutBounds_AreValidated(int timeout, bool valid) => Run(async f =>
    {
        var b = f.Settings.Value.Ocr.Providers[0];
        var result = await f.Coordinator.CommitAsync(Plan(Update(b, b with { TimeoutSeconds = timeout })), TestContext.Current.CancellationToken);
        (result.Status == SettingsEditStatus.Applied).ShouldBe(valid);
    });

    [Theory][InlineData(-0.1, false)][InlineData(0, true)][InlineData(0.9, true)][InlineData(0.91, false)][InlineData(double.NaN, false)][InlineData(double.PositiveInfinity, false)]
    public Task ScreenshotOpacity_IsFiniteAndBounded(double value, bool valid) => Run(async f =>
    {
        var result = await f.Coordinator.CommitAsync(Plan() with { Screenshot = new(MaskOpacity: new(f.Settings.Value.Screenshot.MaskOpacity, value)) }, TestContext.Current.CancellationToken);
        (result.Status == SettingsEditStatus.Applied).ShouldBe(valid);
        f.Settings.Updates.ShouldBe(valid ? 1 : 0);
    });

    [Fact]
    public Task NoOpAndScreenshotOnly_DoNotValidateUneditedHistoricalOcr() => Run(async f =>
    {
        f.Settings.Value = new() { Ocr = new() { ActiveProviderId = "missing", TextLayout = (OcrTextLayout)99, Providers = [] } };
        (await f.Coordinator.CommitAsync(Plan() with { Ocr = new(), Screenshot = new() }, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Updates.ShouldBe(0);
        (await f.Coordinator.CommitAsync(Plan() with { Screenshot = new(MaskOpacity: new(f.Settings.Value.Screenshot.MaskOpacity, 0.7)) }, TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.Ocr.ActiveProviderId.ShouldBe("missing"); f.OcrObserver.Calls.ShouldBe(1);
    });

    [Theory][InlineData("stage")][InlineData("commit")][InlineData("cancel-before")][InlineData("cancel-after")]
    public Task CredentialFailureAndCancellation_RespectSingleCommitBoundary(string failure) => Run(async f =>
    {
        using var caller = new CancellationTokenSource(); var b = f.Settings.Value.Ocr.Providers[0];
        var second = Profile("second", OcrProviderKind.BaiduCloud);
        var plan = Plan(Update(b, b, new(CredentialEditKind.Replace, new("one", "pair"))), new(OcrProfileChangeKind.Add, null, second, new(CredentialEditKind.Replace, new("two", "pair"))));
        if (failure == "stage") f.SecretStore.BeforeSet = _ => { if (f.SecretStore.Writes.Count == 2) throw new IOException("synthetic private store"); };
        if (failure == "commit") f.Settings.BeforeMutate = () => throw new IOException("synthetic private settings");
        if (failure == "cancel-before") f.SecretStore.BeforeSet = _ => caller.Cancel();
        if (failure == "cancel-after") f.Settings.AfterUpdate = caller.Cancel;
        if (failure == "cancel-before") await Should.ThrowAsync<OperationCanceledException>(() => f.Coordinator.CommitAsync(plan, caller.Token));
        else
        {
            var result = await f.Coordinator.CommitAsync(plan, caller.Token);
            result.Status.ShouldBe(failure == "cancel-after" ? SettingsEditStatus.Applied : SettingsEditStatus.Uncommitted);
        }
        f.Settings.Updates.ShouldBe(failure == "cancel-after" ? 1 : 0);
        if (failure != "cancel-after") f.SecretStore.Values.ShouldBeEmpty();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task ApplyPending_RetryUsesOriginalReceiptWithoutRestaging(bool cleanup) => Run(async f =>
    {
        const string oldSet = "cs-00000000000000000000000000000001";
        var b = f.Settings.Value.Ocr.Providers[0] with { CredentialSetId = oldSet };
        f.Settings.Value = f.Settings.Value with { Ocr = f.Settings.Value.Ocr with { Providers = [b, f.Settings.Value.Ocr.Providers[1]] } };
        f.SecretStore.Values[SecretKeys.CredentialSet(oldSet)] = "old private value";
        if (cleanup) f.SecretStore.BeforeRemove = _ => throw new IOException("synthetic private cleanup"); else f.OtherObserver.Fail = true;
        var result = await f.Coordinator.CommitAsync(Plan(Update(b, b, new(CredentialEditKind.Clear))), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.ApplyPending); result.PendingApply.ShouldNotBeNull();
        f.SecretStore.Values.ShouldContainKey(SecretKeys.CredentialSet(oldSet));
        f.OtherObserver.Fail = false; f.SecretStore.BeforeRemove = null;
        (await f.Coordinator.RetryApplyAsync(result.PendingApply, TestContext.Current.CancellationToken)).IsComplete.ShouldBeTrue();
        (await f.Coordinator.RetryApplyAsync(result.PendingApply, TestContext.Current.CancellationToken)).IsComplete.ShouldBeTrue();
        f.Settings.Updates.ShouldBe(1); f.SecretStore.Writes.Count.ShouldBe(1); f.SecretStore.Values.ShouldNotContainKey(SecretKeys.CredentialSet(oldSet));
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task SharedReferenceAcrossDomains_IncludingDisabledPeer_IsNotDeleted(bool legacy) => Run(async f =>
    {
        const string set = "cs-00000000000000000000000000000002";
        var ocr = Profile("same", OcrProviderKind.BaiduCloud) with { CredentialSetId = legacy ? null : set };
        var peer = new TranslationProviderProfile { Id = "same", Kind = TranslationProviderKind.OpenAICompatible, Model = "", DisplayName = "peer", Enabled = false, CredentialSetId = legacy ? null : set };
        f.Settings.Value = new() { Ocr = new() { Providers = [ocr], ActiveProviderId = "same", FallbackProviderId = null }, Translation = new() { Providers = [peer] } };
        string key = legacy ? SecretKeys.ApiKey("same") : SecretKeys.CredentialSet(set); f.SecretStore.Values[key] = "shared";
        (await f.Coordinator.CommitAsync(Plan(Update(ocr, ocr, new(CredentialEditKind.Clear))), TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.SecretStore.Values.ShouldContainKey(key); f.SecretStore.Removes.ShouldNotContain(key);
    });
}
