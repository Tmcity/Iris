using System.Windows.Controls;
using System.Windows.Data;
using Iris.App.Settings;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

[Collection("AppShutdownerSharedState")]
public sealed class HistorySettingsUiTests
{
    [Theory][InlineData(100)][InlineData(100000)]
    public Task HistoryOnly_UsesExistingSaveAndNoProviderQuota(int max) => Run(async f =>
    {
        f.Show("history"); ((ScrollViewer)f.Window.FindName("HistoryPageScroll")).IsVisible.ShouldBeTrue();
        f.Cloud.Created.ShouldBeEmpty(); f.Local.Created.ShouldBeEmpty();
        for (int i = 0; i < f.Receipts.MaxCapacity; i++)
        { f.Receipts.TryReserveSlot().ShouldBeTrue(); var r = Substitute.For<ISettingsApplyReceipt>(); r.CommitId.Returns(Guid.NewGuid()); f.Receipts.ConvertReservationToPending(r, "synthetic"); }
        f.ExtraObserver.Fail = true;
        f.Vm.History.Enabled.Value = false; f.Vm.History.MaxEntriesText.Value = max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        f.Vm.IsDirty.ShouldBeTrue(); await f.Vm.ExecuteSaveAsync();
        f.Settings.Updates.ShouldBe(1); f.Settings.Value.History.ShouldBe(new HistorySettings { Enabled = false, MaxEntries = max });
        f.SecretStore.Writes.ShouldBe(0); f.Receipts.PendingCount.ShouldBe(f.Receipts.MaxCapacity); f.Vm.IsDirty.ShouldBeFalse();
        // Existing Registry settings subscriptions may refresh instances after any committed settings change.
        f.Cloud.Created.Concat(f.Local.Created).ShouldAllBe(p => p.Tests == 0 && p.Recognitions == 0);
    });

    [Theory][InlineData("")][InlineData("99")][InlineData("100001")][InlineData("3.5")][InlineData("NaN")][InlineData("2147483648")]
    public Task InvalidCurrentText_IsFlushedAndRetainedWithoutUsingOldValue(string text) => Run(async f =>
    {
        f.Show("history"); var box = (TextBox)f.Window.FindName("HistoryMaxEntriesTextBox");
        BindingOperations.SetBinding(box, TextBox.TextProperty, new Binding("History.MaxEntriesText.Value")
        { Source = f.Vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        box.SetCurrentValue(TextBox.TextProperty, text); await f.Vm.ExecuteSaveAsync();
        f.Settings.Updates.ShouldBe(0); f.SecretStore.Writes.ShouldBe(0);
        f.Vm.History.MaxEntriesText.Value.ShouldBe(text); f.Vm.History.ErrorMessage.ShouldBe(UiText.HistoryMaxError);
    });

    [Fact]
    public Task ExternalUpdates_MergeCleanFieldsAndPreserveDirtyBaseForConflict() => Run(async f =>
    {
        f.Show("history"); f.Vm.History.MaxEntriesText.Value = "333";
        f.Settings.External(s => s with { History = s.History with { Enabled = false, MaxEntries = 444 } });
        await f.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        f.Vm.History.Enabled.Value.ShouldBeFalse(); f.Vm.History.MaxEntriesText.Value.ShouldBe("333");
        await f.Vm.ExecuteSaveAsync(); f.Vm.IsConflict.ShouldBeTrue(); f.Settings.Updates.ShouldBe(0);
        f.Vm.ExecuteReload(); f.Vm.History.MaxEntriesText.Value.ShouldBe("444"); f.Vm.IsDirty.ShouldBeFalse();
    });

    [Fact]
    public Task CombinedSave_FreezesHistoryAndPreservesApplyPendingAndCredentials() => Run(async f =>
    {
        f.Show("history"); var gate = f.NewGate(); f.Settings.BeforeUpdate = gate.Pause;
        f.Vm.History.MaxEntriesText.Value = "777"; f.Vm.LogLevel = "Warning"; f.Vm.Screenshot.ShowMagnifier.Value = false;
        f.Vm.Ocr.LanguageHint.Value = "en";
        f.Vm.SelectedOcrProfile!.Credentials.Value = CredentialEditKind.Replace;
        f.Vm.SelectedOcrProfile.SetReplacementCredentials("synthetic-api", "synthetic-secret");
        f.ExtraObserver.Fail = true;
        var save = f.Track(f.Vm.ExecuteSaveAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Vm.History.MaxEntriesText.Value = "999"; f.Vm.History.MaxEntriesText.Value.ShouldBe("777");
        gate.Open(); await save; f.Settings.Updates.ShouldBe(1); f.Receipts.PendingCount.ShouldBe(1);
        f.Settings.Value.History.MaxEntries.ShouldBe(777); f.Settings.Value.General.LogLevel.ShouldBe("Warning");
        f.Settings.Value.Screenshot.ShowMagnifier.ShouldBeFalse(); f.Settings.Value.Ocr.LanguageHint.ShouldBe("en");
        f.Vm.SelectedOcrProfile.ReplacementApiKey.ShouldBeNull(); int writes = f.SecretStore.Writes;
        f.ExtraObserver.Fail = false; await f.Vm.ExecuteRetryPendingApplyAsync();
        f.Settings.Updates.ShouldBe(1); f.SecretStore.Writes.ShouldBe(writes); f.Receipts.PendingCount.ShouldBe(0);
    });

    [Fact]
    public Task HistorySave_WaitsForOcrCleanupAndCloseWinsBeforeCommit() => Run(async f =>
    {
        f.Show("history"); var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        var test = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Vm.History.Enabled.Value = false; var save = f.Track(f.Vm.ExecuteSaveAsync());
        var close = f.Track(f.Session.ShutdownAsync());
        save.IsCompleted.ShouldBeFalse(); close.IsCompleted.ShouldBeFalse(); f.Settings.Updates.ShouldBe(0);
        gate.Open(); await test; await save; await close; f.Settings.Updates.ShouldBe(0);
    });
}
