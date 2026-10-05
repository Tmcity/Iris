using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using Iris.App.Settings;
using Iris.App.Services;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Settings;

public sealed class SelectionSettingsUiTests
{
    [Fact]
    public Task PageAndDraft_AreSideEffectFree_AndExposeSelectionHotkey() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show(); f.Vm.CurrentPageKey.ShouldBe("selection"); f.Vm.IsDirty.ShouldBeFalse();
        f.Vm.Hotkeys.Single(h => h.Action == IrisAction.SelectionTranslate).KeyComboText.ShouldBe("Ctrl+Alt+D");
        f.Reader.ReceivedCalls().ShouldBeEmpty(); f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.NativeHotkeys.RegisterHotKeyCallCount.ShouldBe(0);
        f.Vm.Selection.ClipboardTimeoutText = "800"; f.Vm.Selection.ReadOrderChoice = 3;
        f.Vm.Selection.ClipboardUnsafeProcessesText = " app.exe \n\nAPP.EXE\nhelper.bin";
        f.Vm.Selection.RestoreClipboard = false; f.Vm.Selection.OpenInputWhenEmpty = false;
        f.Vm.IsDirty.ShouldBeTrue(); await f.Vm.ExecuteSaveAsync();
        f.Store.Updates.ShouldBe(1, f.Vm.StatusMessage); f.Vm.IsDirty.ShouldBeFalse();
        var current = f.Store.Current.Selection;
        current.ReadOrder.ShouldBe(new[] { SelectionReadMethod.Clipboard }); current.ClipboardTimeoutMs.ShouldBe(800);
        current.ClipboardUnsafeProcesses.ShouldBe(new[] { "app.exe", "helper.bin" }); current.RestoreClipboard.ShouldBeFalse(); current.OpenInputWhenEmpty.ShouldBeFalse();
        f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty(); f.Receipts.PendingCount.ShouldBe(0);
        f.Reader.ReceivedCalls().ShouldBeEmpty(); f.AutoStart.DidNotReceive().SetEnabled(Arg.Any<bool>());
        await f.ReopenAsync(); f.Vm.Selection.ClipboardTimeoutText.ShouldBe("800"); f.Vm.IsDirty.ShouldBeFalse();
    });

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("100.5")]
    [InlineData("99")]
    [InlineData("2001")]
    public Task SaveFlushesCurrentText_AndNeverSavesPreviousInteger(string invalid) => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show(); var box = (TextBox)f.Window.FindName("SelectionTimeoutTextBox");
        BindingOperations.SetBinding(box, TextBox.TextProperty, new Binding("Selection.ClipboardTimeoutText")
        { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        box.SetCurrentValue(TextBox.TextProperty, invalid);
        f.Vm.Selection.ClipboardTimeoutText.ShouldBe("400");
        await f.Vm.ExecuteSaveAsync();
        f.Vm.Selection.ClipboardTimeoutText.ShouldBe(invalid); f.Vm.Selection.HasError.ShouldBeTrue(); f.Vm.IsDirty.ShouldBeTrue();
        f.Store.Updates.ShouldBe(0); f.Vm.IsSaving.ShouldBeFalse(); f.Vm.CanEdit.ShouldBeTrue();
    });

    [Fact]
    public Task TypingBeforeFocusLeaves_IsDirtyAndProtectedFromExternalMerge() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show();
        var box = (TextBox)f.Window.FindName("SelectionTimeoutTextBox");
        var names = (TextBox)f.Window.FindName("SelectionProcessesTextBox");
        box.SetCurrentValue(TextBox.TextProperty, "800"); names.SetCurrentValue(TextBox.TextProperty, "local.exe");
        f.Vm.IsDirty.ShouldBeTrue();
        f.Store.External(s => s with { Selection = s.Selection with { ClipboardTimeoutMs = 600, ClipboardUnsafeProcesses = ["external.exe"] } });
        f.Vm.Selection.ClipboardTimeoutText.ShouldBe("800"); f.Vm.Selection.ClipboardUnsafeProcessesText.ShouldBe("local.exe");
        await f.Vm.ExecuteSaveAsync(); f.Vm.IsConflict.ShouldBeTrue();
    });

    [Fact]
    public Task ExternalMerge_PreservesDirtyBaseAndMergesCleanFields() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Vm.Selection.ClipboardTimeoutText = "800";
        f.Store.External(s => s with { Selection = s.Selection with { ClipboardTimeoutMs = 600, RestoreClipboard = false, MinDragDistancePx = 41 } });
        f.Vm.Selection.ClipboardTimeoutText.ShouldBe("800"); f.Vm.Selection.RestoreClipboard.ShouldBeFalse();
        await f.Vm.ExecuteSaveAsync(); f.Vm.IsConflict.ShouldBeTrue(); f.Vm.IsDirty.ShouldBeTrue(); f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(600);
        f.Vm.ExecuteReload(); f.Vm.IsDirty.ShouldBeFalse(); f.Vm.Selection.ClipboardTimeoutText.ShouldBe("600");
        f.Vm.Selection.OpenInputWhenEmpty = false; await f.Vm.ExecuteSaveAsync();
        f.Store.Current.Selection.MinDragDistancePx.ShouldBe(41);
    });

    [Fact]
    public Task UneditedInvalidHistory_DoesNotBlockAnotherPage() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Store.External(s => s with { Selection = s.Selection with { ReadOrder = [(SelectionReadMethod)99], ClipboardTimeoutMs = -5, ClipboardUnsafeProcesses = [" old.exe "] } });
        f.Vm.Selection.ReadOrderChoice.ShouldBeNull(); f.Vm.IsDirty.ShouldBeFalse();
        f.Vm.LogLevel = "Warning"; await f.Vm.ExecuteSaveAsync();
        f.Vm.IsDirty.ShouldBeFalse(); f.Store.Current.Selection.ReadOrder.Single().ShouldBe((SelectionReadMethod)99);
        f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(-5);
        f.Vm.Selection.ReadOrderChoice = 2; f.Vm.Selection.ClipboardTimeoutText = "200";
        f.Vm.Selection.ClipboardUnsafeProcessesText = "old.exe"; await f.Vm.ExecuteSaveAsync();
        f.Store.Current.Selection.ClipboardUnsafeProcesses.ShouldBe(new[] { "old.exe" });
    });

    [Fact]
    public Task SaveFailure_KeepsDraft_AndSelectionOnlyBypassesReceiptQuota() => SelectionSettingsTestScope.Run(async f =>
    {
        for (int i = 0; i < f.Receipts.MaxCapacity; i++)
        {
            f.Receipts.TryReserveSlot().ShouldBeTrue();
            var receipt = Substitute.For<ISettingsApplyReceipt>(); receipt.CommitId.Returns(Guid.NewGuid());
            f.Receipts.ConvertReservationToPending(receipt, "test");
        }
        f.Vm.Selection.ClipboardTimeoutText = "900"; f.Store.Failure = new IOException("write failed");
        await f.Vm.ExecuteSaveAsync(); f.Vm.IsDirty.ShouldBeTrue(); f.Vm.Selection.ClipboardTimeoutText.ShouldBe("900");
        f.Store.Failure = null; await f.Vm.ExecuteSaveAsync();
        f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(900); f.Vm.IsDirty.ShouldBeFalse(); f.Receipts.PendingCount.ShouldBe(f.Receipts.MaxCapacity);
        f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SavedPendingApply_AdvancesSelectionBaseline_AndRetryPreservesNewDraft(bool autoStartFailure) => SelectionSettingsTestScope.Run(async f =>
    {
        f.Vm.AddProfileFromPreset(TranslationProviderPresets.All[0]);
        var profile = f.Vm.Profiles.Last(); profile.Model = "model"; profile.CredentialEditKind = CredentialEditKind.Replace; profile.SetReplacementApiKey("fixture-only");
        f.Vm.Selection.ClipboardTimeoutText = "800";
        f.Observer.RefreshAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("observer failed"));
        if (autoStartFailure)
        {
            f.Vm.LaunchAtStartup = true;
            f.AutoStart.When(a => a.SetEnabled(true)).Do(_ => throw new IOException("fake startup failure"));
        }
        await f.Vm.ExecuteSaveAsync();
        f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(800); f.Vm.IsDirty.ShouldBeFalse(); f.Vm.CanRetryPendingApply.ShouldBeTrue();
        f.Vm.Selection.ClipboardTimeoutText = "900";
        var entered = Gate(); var release = Gate(); f.Releases.Add(() => release.TrySetResult());
        f.Observer.RefreshAsync(Arg.Any<CancellationToken>()).Returns(async _ => { entered.TrySetResult(); await release.Task; });
        var retry = f.Track(f.Vm.ExecuteRetryPendingApplyAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Vm.Selection.IsFrozen.ShouldBeTrue();
        f.Vm.Selection.ClipboardTimeoutText = "1000"; f.Vm.Selection.ClipboardTimeoutText.ShouldBe("900");
        release.TrySetResult(); await retry;
        f.Vm.Selection.ClipboardTimeoutText.ShouldBe("900"); f.Vm.IsDirty.ShouldBeTrue(); f.Receipts.PendingCount.ShouldBe(0);
    });

    [Fact]
    public Task ClosingDuringSave_FreezesAndWaitsForActualWork() => SelectionSettingsTestScope.Run(f => ClosingScenario(f, null));

    private static async Task ClosingScenario(SelectionSettingsTestScope f, Exception? injected)
    {
        f.Show(); f.Vm.Selection.ClipboardTimeoutText = "800";
        var entered = Gate(); var release = Gate(); f.Releases.Add(() => release.TrySetResult());
        f.Store.BeforeUpdate = async _ => { entered.TrySetResult(); await release.Task; };
        var save = f.Track(f.Vm.ExecuteSaveAsync()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Vm.Selection.IsFrozen.ShouldBeTrue(); f.Vm.Selection.ClipboardTimeoutText = "1200"; f.Vm.Selection.ClipboardTimeoutText.ShouldBe("800");
        var close = f.Track(f.WindowSession.ShutdownAsync()); close.IsCompleted.ShouldBeFalse();
        if (injected != null) throw injected;
        release.TrySetResult(); await save; await close;
        f.WindowSession.IsOpen.ShouldBeFalse(); f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureWhileSaveAndCloseWait_ReleasesGateAndPreservesErrors(bool cleanupError)
    {
        var body = new InvalidOperationException("body injection"); var cleanup = new IOException("cleanup injection");
        SelectionSettingsTestScope? observed = null;
        Task Scenario(SelectionSettingsTestScope f)
        {
            observed = f; if (cleanupError) f.AfterCleanup = () => throw cleanup;
            return ClosingScenario(f, body);
        }
        if (cleanupError)
        {
            var error = await Should.ThrowAsync<AggregateException>(() => SelectionSettingsTestScope.Run(Scenario));
            error.InnerExceptions.ShouldContain(body); error.InnerExceptions.ShouldContain(cleanup);
        }
        else (await Should.ThrowAsync<InvalidOperationException>(() => SelectionSettingsTestScope.Run(Scenario))).ShouldBeSameAs(body);
        observed!.WindowSession.IsOpen.ShouldBeFalse(); observed.Tasks.ShouldAllBe(task => task.IsCompleted);
    }

    [Fact]
    public Task SelectionHotkeyRow_UsesRealRecorderAndOneJointSave() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show(); await f.Hotkeys.StartAsync();
        var item = f.Vm.Hotkeys.Single(h => h.Action == IrisAction.SelectionTranslate);
        var recording = f.Track(f.Vm.StartRecordingAsync(item));
        await f.NativeHook.PumpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.NativeHook.KeyAsync(0xA2); await f.NativeHook.KeyAsync(0xA4); await f.NativeHook.KeyAsync(0x4A);
        await f.NativeHook.KeyAsync(0x4A, true); await f.NativeHook.KeyAsync(0xA4, true); await f.NativeHook.KeyAsync(0xA2, true);
        await recording; item.TargetBinding!.Key.ShouldBe("J"); f.NativeHook.Unhooks.ShouldBe(1);
        f.Vm.Selection.ClipboardTimeoutText = "750"; await f.Vm.ExecuteSaveAsync();
        f.Store.Updates.ShouldBe(1); f.Store.Current.Hotkeys[IrisAction.SelectionTranslate].Key.ShouldBe("J");
        f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(750);
    });

    [Fact]
    public Task SaveToActualHandler_NewCallsUseSavedOptions_OldReadKeepsSnapshot() => SelectionSettingsTestScope.Run(async f =>
    {
        var entered = Gate(); var release = Gate(); f.Releases.Add(() => release.TrySetResult());
        var options = new List<SelectionReadOptions>();
        f.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            options.Add(call.Arg<SelectionReadOptions>()); if (options.Count == 1) { entered.TrySetResult(); await release.Task; } return null;
        });
        var op = Substitute.For<ITranslateWindowOperation>(); op.CancellationToken.Returns(CancellationToken.None);
        f.Presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(op);
        var desktop = Substitute.For<IDesktopContext>();
        desktop.GetForegroundApp().Returns(new ForegroundAppInfo("test.exe", Environment.ProcessId + 1, null, 1));
        var handler = new SelectionTranslateHandler(f.Presenter, f.Reader, f.Store, desktop, NullLogger<SelectionTranslateHandler>.Instance);
        var read = f.Track(handler.ExecuteAsync(new(ActionTrigger.Hotkey), CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Vm.Selection.ClipboardTimeoutText = "950"; f.Vm.Selection.ReadOrderChoice = 3;
        await f.Vm.ExecuteSaveAsync(); release.TrySetResult(); await read;
        await handler.ExecuteAsync(new(ActionTrigger.Hotkey), CancellationToken.None);
        options[0].ClipboardTimeoutMs.ShouldBe(400); options[0].Order.Count.ShouldBe(2);
        options[1].ClipboardTimeoutMs.ShouldBe(950); options[1].Order.ShouldBe(new[] { SelectionReadMethod.Clipboard });
    });

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
