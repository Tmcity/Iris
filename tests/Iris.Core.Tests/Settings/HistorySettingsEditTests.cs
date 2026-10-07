using Iris.Core.Errors;
using Iris.Core.Settings;
using Shouldly;
using static Iris.Core.Tests.Settings.OcrEditingTestScope;

namespace Iris.Core.Tests.Settings;

public sealed class HistorySettingsEditTests
{
    private static SettingsEditPlan Edit(HistorySettingsPatch? patch) => new(new(), []) { History = patch };

    [Theory][InlineData(100)][InlineData(100000)]
    public Task InclusiveLimits_CommitOnceWithoutProviderObservers(int max) => Run(async f =>
    {
        var baseline = f.Settings.Value.History;
        f.OtherObserver.Fail = true;
        var result = await f.Coordinator.CommitAsync(Edit(new(new(baseline.Enabled, !baseline.Enabled), new(baseline.MaxEntries, max))));
        result.Status.ShouldBe(SettingsEditStatus.Applied); result.PendingApply.ShouldBeNull();
        f.Settings.Value.History.ShouldBe(baseline with { Enabled = !baseline.Enabled, MaxEntries = max });
        f.Settings.Updates.ShouldBe(1); f.OtherObserver.Calls.ShouldBe(0); f.OcrObserver.Calls.ShouldBe(0);
        f.SecretStore.Writes.ShouldBeEmpty(); f.SecretStore.Removes.ShouldBeEmpty();
    });

    [Theory][InlineData(99, 100)][InlineData(100001, 100)][InlineData(100, 99)][InlineData(100, 100001)]
    public Task InvalidBaseOrTarget_IsRejectedBeforeStagingOrUpdate(int baseline, int target) => Run(async f =>
    {
        var p = f.Settings.Value.Ocr.Providers[0];
        var plan = Plan(Update(p, p, new(CredentialEditKind.Replace, new("synthetic-api", "synthetic-secret")))) with
        { History = new(MaxEntries: new(baseline, target)) };
        var result = await f.Coordinator.CommitAsync(plan);
        result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument); f.Settings.Updates.ShouldBe(0);
        f.SecretStore.Writes.ShouldBeEmpty(); f.OtherObserver.Calls.ShouldBe(0);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task HistoryConflict_IsCheckedBeforeStageAndInsideUpdate(bool duringUpdate) => Run(async f =>
    {
        int baseline = f.Settings.Value.History.MaxEntries;
        void Change() => f.Settings.Value = f.Settings.Value with { History = f.Settings.Value.History with { MaxEntries = 333 } };
        if (duringUpdate) f.Settings.BeforeMutate = Change; else Change();
        var result = await f.Coordinator.CommitAsync(Edit(new(MaxEntries: new(baseline, 222))));
        result.IsConflict.ShouldBeTrue(); f.Settings.Value.History.MaxEntries.ShouldBe(333);
        f.Settings.Updates.ShouldBe(0); f.SecretStore.Writes.ShouldBeEmpty();
    });

    [Fact]
    public Task PartialPatch_PreservesLatestUneditedFieldsAndSameTarget() => Run(async f =>
    {
        var old = f.Settings.Value;
        f.Settings.BeforeMutate = () => f.Settings.Value = old with { History = old.History with { Enabled = false }, General = old.General with { LogLevel = "Warning" } };
        (await f.Coordinator.CommitAsync(Edit(new(MaxEntries: new(old.History.MaxEntries, 500))))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Settings.Value.History.ShouldBe(old.History with { Enabled = false, MaxEntries = 500 });
        f.Settings.Value.General.LogLevel.ShouldBe("Warning"); f.Settings.BeforeMutate = null;
        var same = f.Settings.Value;
        (await f.Coordinator.CommitAsync(Edit(new(MaxEntries: new(old.History.MaxEntries, 500))))).IsConflict.ShouldBeFalse();
        f.Settings.Value.ShouldBeSameAs(same);
    });

    [Fact]
    public Task NullAndEmptyHistory_AreNoOpAndDoNotExposeValues() => Run(async f =>
    {
        await f.Coordinator.CommitAsync(Edit(null)); await f.Coordinator.CommitAsync(Edit(new()));
        f.Settings.Updates.ShouldBe(0); Edit(new(MaxEntries: new(100, 98765))).ToString().ShouldBe("SettingsEditPlan");
        new HistorySettingsPatch(MaxEntries: new(100, 98765)).ToString().ShouldBe("HistorySettingsPatch");
    });

    [Fact]
    public Task CombinedOcrAndHistory_CommitOnceAndRetryOriginalReceipt() => Run(async f =>
    {
        var p = f.Settings.Value.Ocr.Providers[0]; f.OtherObserver.Fail = true;
        var plan = Plan(Update(p, p with { DisplayName = "updated" }, new(CredentialEditKind.Replace, new("synthetic-api", "synthetic-secret")))) with
        { History = new(MaxEntries: new(f.Settings.Value.History.MaxEntries, 4321)), General = new(LogLevel: new("Information", "Warning")) };
        var result = await f.Coordinator.CommitAsync(plan);
        result.Status.ShouldBe(SettingsEditStatus.ApplyPending); f.Settings.Updates.ShouldBe(1);
        f.Settings.Value.History.MaxEntries.ShouldBe(4321); f.Settings.Value.General.LogLevel.ShouldBe("Warning");
        int writes = f.SecretStore.Writes.Count; f.OtherObserver.Fail = false;
        (await f.Coordinator.RetryApplyAsync(result.PendingApply!)).IsComplete.ShouldBeTrue();
        f.Settings.Updates.ShouldBe(1); f.SecretStore.Writes.Count.ShouldBe(writes);
    });
}
