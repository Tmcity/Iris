using System.Windows.Controls;
using Iris.App.Selection;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.Selection;
using Iris.Core.Actions;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Iris.App.Tests.Settings;

public sealed class MouseSettingsUiTests
{
    [Theory]
    [InlineData("abc", "3000")][InlineData("1", "3000")][InlineData("51", "3000")][InlineData("6", "499")][InlineData("6", "10001")]
    public Task InvalidIntegerText_NeverCommitsPreviousValue(string drag, string hide) => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show();
        ((TextBox)f.Window.FindName("MouseDragTextBox")).SetCurrentValue(TextBox.TextProperty, drag);
        ((TextBox)f.Window.FindName("MouseHideTextBox")).SetCurrentValue(TextBox.TextProperty, hide);
        await f.Vm.ExecuteSaveAsync(); f.Store.Updates.ShouldBe(0); f.Vm.Selection.HasError.ShouldBeTrue();
        f.Vm.Selection.MinDragDistanceText.ShouldBe(drag); f.Vm.Selection.IconAutoHideText.ShouldBe(hide); f.Vm.IsDirty.ShouldBeTrue();
    });
    [Fact]
    public Task DirtyMouseFields_SurviveExternalMergeAndOneJointSave() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show();
        ((TextBox)f.Window.FindName("MouseDragTextBox")).SetCurrentValue(TextBox.TextProperty, "18");
        ((TextBox)f.Window.FindName("MouseExcludedTextBox")).SetCurrentValue(TextBox.TextProperty, " app.EXE \nAPP.exe");
        f.Store.External(s => s with { Selection = s.Selection with { IconAutoHideMs = 6000, RequireIBeamCursor = false } });
        f.Vm.Selection.MinDragDistanceText.ShouldBe("18"); f.Vm.Selection.IconAutoHideText.ShouldBe("6000");
        f.Vm.Selection.MouseMode = MouseSelectionMode.ShowIcon; f.Vm.Selection.ClipboardTimeoutText = "777";
        await f.Vm.ExecuteSaveAsync(); f.Store.Updates.ShouldBe(1); f.Vm.IsDirty.ShouldBeFalse();
        f.Store.Current.Selection.ExcludedProcesses.ShouldBe(new[] { "app.EXE" }); f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(777);
        f.Store.Current.Selection.MinDragDistancePx.ShouldBe(18); f.Store.Current.Selection.IconAutoHideMs.ShouldBe(6000);
    });
    [Fact]
    public Task ToggleRow_DefaultAbsent_UsesActualRecorderWithoutSelfConflict() => SelectionSettingsTestScope.Run(async f =>
    {
        f.Show(); await f.Hotkeys.StartAsync();
        var item = f.Vm.Hotkeys.Single(h => h.Action == IrisAction.ToggleMouseSelection);
        item.BaseBinding.ShouldBeNull(); item.IsDirty.ShouldBeFalse();
        var recording = f.Track(f.Vm.StartRecordingAsync(item)); await f.NativeHook.PumpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.NativeHook.KeyAsync(0xA2); await f.NativeHook.KeyAsync(0xA4); await f.NativeHook.KeyAsync(0x4D);
        await f.NativeHook.KeyAsync(0x4D, true); await f.NativeHook.KeyAsync(0xA4, true); await f.NativeHook.KeyAsync(0xA2, true);
        await recording; f.Vm.Selection.MouseMode = MouseSelectionMode.ShowIcon; await f.Vm.ExecuteSaveAsync();
        f.Store.Updates.ShouldBe(1); f.Store.Current.Hotkeys[IrisAction.ToggleMouseSelection].Key.ShouldBe("M"); item.HasDraftError.ShouldBeFalse();
    });
    [Fact]
    public Task SavedNativeFailure_RetryUsesLatestSavedAndDoesNotSubmitDraft_ClosingPageKeepsMonitor() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Controller.StartAsync();
        using var scope = f.Services.CreateScope(); var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        f.Mouse.InstallFailure = true;
        vm.Selection.MouseMode = MouseSelectionMode.ShowIcon;
        await vm.ExecuteSaveAsync();
        f.Settings.Current.Selection.MouseMode.ShouldBe(MouseSelectionMode.ShowIcon); vm.IsDirty.ShouldBeFalse();
        vm.CanRetryMouse.ShouldBeTrue(); vm.MouseRuntimeStatus.ShouldBe(UiText.MouseApplyFailed);
        vm.Selection.MinDragDistanceText = "20";
        int saves = f.Settings.Updates;
        f.Mouse.InstallFailure = false; await vm.ExecuteRetryMouseAsync();
        f.Settings.Updates.ShouldBe(saves); f.Settings.Current.Selection.MinDragDistancePx.ShouldBe(6);
        vm.Selection.MinDragDistanceText.ShouldBe("20"); vm.IsDirty.ShouldBeTrue(); vm.CanRetryMouse.ShouldBeFalse();
        vm.StopAcceptance(); await vm.WaitForActiveWorkAsync(); vm.Dispose(); f.Monitor.IsRunning.ShouldBeTrue();
    });
    [Fact]
    public Task RetryFreezesDraftsAndPageCloseWaitsActualLateInstallation() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Controller.StartAsync();
        using var scope = f.Services.CreateScope(); var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        f.Mouse.InstallFailure = true; vm.Selection.MouseMode = MouseSelectionMode.ShowIcon; await vm.ExecuteSaveAsync();
        f.Mouse.InstallFailure = false; f.Mouse.InstallGate = f.Gate();
        var retry = f.Track(vm.ExecuteRetryMouseAsync());
        await f.Mouse.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.Selection.MinDragDistanceText = "22"; vm.Selection.MinDragDistanceText.ShouldBe("6");
        vm.StopAcceptance(); var close = f.Track(vm.WaitForActiveWorkAsync()); close.IsCompleted.ShouldBeFalse();
        f.Mouse.InstallGate.Release(); await retry; await close; vm.Dispose();
    });
}
