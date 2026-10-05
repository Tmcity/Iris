using System.Windows;
using System.Windows.Controls;
using Iris.App.Capture;
using Iris.App.Tests.Selection;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.App.Tests.Capture;

public sealed class ScreenshotPreparationTests
{
    [Fact]
    public Task UnusedAndNoWindow_DoNotCreateResources() => PreparationTestScope.Run(async f =>
    {
        await using (await f.Preparation.PrepareAsync(true))
        {
            f.Settings.ActiveSession.ShouldBeNull(); f.Mouse.Presenter.Window.ShouldBeNull();
            f.Native.Installs.ShouldBe(0); f.Mouse.Mouse.Calls.ShouldBeEmpty();
            f.Mouse.Factory.Windows.ShouldBeEmpty();
        }
        await f.Preparation.StopAsync();
        f.Recording.CanStartRecording.ShouldBeFalse();
    });

    [Theory]
    [InlineData(true)][InlineData(false)]
    public Task VisibilityAndDraftAndOperation_ArePreserved(bool hide) => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings();
        var session = f.Settings.ActiveSession!;
        session.ViewModel.Selection.ClipboardTimeoutText = "777";
        using var op = f.Mouse.Presenter.TryBeginOperation()!;
        (await f.Mouse.Presenter.TryShowAsync(op, new() { Text = "synthetic text", FocusInput = false })).ShouldBeTrue();
        var window = f.Mouse.Presenter.Window!;
        var lease = await f.Preparation.PrepareAsync(hide);
        window.IsVisible.ShouldBe(!hide); session.Window.IsVisible.ShouldBe(!hide);
        op.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        session.ViewModel.CanEdit.ShouldBeTrue(); session.ViewModel.Hotkeys.ShouldAllBe(h => !h.CanRecord);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        window.IsVisible.ShouldBeTrue(); session.Window.IsVisible.ShouldBeTrue();
        f.Settings.ActiveSession.ShouldBeSameAs(session);
        session.ViewModel.Selection.ClipboardTimeoutText.ShouldBe("777");
        f.Mouse.Presenter.ViewModel!.InputText.ShouldBe("synthetic text");
        op.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        session.ViewModel.Hotkeys.ShouldAllBe(h => h.CanRecord);
    });

    [Fact]
    public Task HiddenWindows_AreNotRestored() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var session = f.Settings.ActiveSession!; session.Window.Hide();
        using var op = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(op, new() { FocusInput = false });
        await f.Mouse.Presenter.HideAsync();
        await using (await f.Preparation.PrepareAsync(true)) { }
        session.Window.IsVisible.ShouldBeFalse(); f.Mouse.Presenter.Window!.IsVisible.ShouldBeFalse();
    });

    [Fact]
    public Task ReopenSettings_ShowsSameSession_AndRevokesOldRestore() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var session = f.Settings.ActiveSession!;
        var lease = await f.Preparation.PrepareAsync(true);
        session.Window.IsVisible.ShouldBeFalse();
        await f.Settings.ShowAsync("selection");
        lease.Reason.ShouldBe(PreparationRevocation.DisplayIntent);
        f.Settings.ActiveSession.ShouldBeSameAs(session); session.Window.IsVisible.ShouldBeTrue();
        session.ViewModel.CurrentPageKey.ShouldBe("selection");
        session.Window.Hide(); // A stale lease must not undo a later owner's visibility.
        await lease.DisposeAsync(); session.Window.IsVisible.ShouldBeFalse();
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task EffectivePublicationOrNewOperation_RevokesLease(bool newOperation) => PreparationTestScope.Run(async f =>
    {
        using var op = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(op, new() { FocusInput = false });
        var lease = await f.Preparation.PrepareAsync(true);
        using var next = newOperation ? f.Mouse.Presenter.TryBeginOperation()! : null;
        await f.Mouse.Presenter.TryShowAsync(next ?? op, new() { Text = "new intent", FocusInput = false });
        lease.Reason.ShouldBe(PreparationRevocation.DisplayIntent);
        await f.Mouse.Presenter.HideAsync(); await lease.DisposeAsync();
        f.Mouse.Presenter.Window!.IsVisible.ShouldBeFalse();
    });

    [Fact]
    public Task BusyAndPreCanceled_DoNotAffectExistingLease_StopDoesNotRestore() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var window = f.Settings.ActiveSession!.Window;
        var lease = await f.Preparation.PrepareAsync(true);
        (await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true))).Code.ShouldBe(ErrorCode.CaptureFailed);
        await Should.ThrowAsync<OperationCanceledException>(() => f.Preparation.PrepareAsync(true, new CancellationToken(true)));
        lease.Reason.ShouldBe(PreparationRevocation.None);
        var first = f.Preparation.StopAsync(); var second = f.Preparation.StopAsync();
        await Task.WhenAll(first, second); await lease.DisposeAsync();
        window.IsVisible.ShouldBeFalse(); f.Recording.CanStartRecording.ShouldBeFalse();
        lease.Reason.ShouldBe(PreparationRevocation.Shutdown);
    });

    [Fact]
    public Task VisibleThenHideFailure_RestoresBoth_WithoutCancelingOperation() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); using var op = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(op, new() { FocusInput = false });
        f.Preparation.Visibility = new Visibility { FailHideAt = 2 };
        var error = await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true));
        error.Code.ShouldBe(ErrorCode.CaptureFailed); error.ToString().ShouldNotContain("private window");
        f.Settings.ActiveSession!.Window.IsVisible.ShouldBeTrue(); f.Mouse.Presenter.Window!.IsVisible.ShouldBeTrue();
        op.CancellationToken.IsCancellationRequested.ShouldBeFalse();
    });

    [Fact]
    public Task RestoreFailure_RetainsIdentityForExplicitRetry() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var visibility = new Visibility { FailShow = true }; f.Preparation.Visibility = visibility;
        var lease = await f.Preparation.PrepareAsync(true);
        await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
        f.Settings.ActiveSession!.Window.IsVisible.ShouldBeFalse();
        visibility.FailShow = false;
        await f.Preparation.RetryCleanupAsync(); f.Settings.ActiveSession.Window.IsVisible.ShouldBeTrue();
    });

    [Fact]
    public Task ActualRecording_UnhookGateBlocksPreparation_NewIntentCancelsBeforeHide() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var vm = f.Settings.ActiveSession!.ViewModel; var item = vm.Hotkeys[0];
        var gate = f.Mouse.Gate(); f.Native.BeforeUnhook = gate.Block;
        var recording = vm.StartRecordingAsync(item); f.Work.Add(recording);
        await f.Native.Inner.PumpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var prepare = f.Preparation.PrepareAsync(true); f.Work.Add(prepare);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        prepare.IsCompleted.ShouldBeFalse(); f.Settings.ActiveSession.Window.IsVisible.ShouldBeTrue();
        await vm.StartRecordingAsync(vm.Hotkeys[1]); f.Native.Installs.ShouldBe(1);
        await f.Settings.ShowAsync("selection");
        gate.Release();
        await Should.ThrowAsync<OperationCanceledException>(() => prepare);
        await recording;
        vm.IsAnyRecording.ShouldBeFalse(); vm.CanEdit.ShouldBeTrue();
        f.Settings.ActiveSession.Window.IsVisible.ShouldBeTrue(); item.IsDirty.ShouldBeFalse();
    });

    [Fact]
    public Task CandidatePause_RejectsOldClickAndNewGestures_ReleaseAcceptsFreshOnly() => PreparationTestScope.Run(async f =>
    {
        await f.Mouse.Mode(MouseSelectionMode.ShowIcon); await f.Mouse.Controller.StartAsync(); await f.Mouse.ShowCandidate();
        int reads = f.Mouse.Platform.Runner.TotalStartCalls;
        var window = f.Mouse.Factory.Windows.Single();
        var lease = await f.Preparation.PrepareAsync(false);
        window.IsVisible.ShouldBeFalse(); window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await f.Mouse.Mouse.EnqueueAsync(Iris.Platform.Windows.Hooks.MouseMessage.LeftDown, 100, 0);
        await f.Mouse.Mouse.EnqueueAsync(Iris.Platform.Windows.Hooks.MouseMessage.LeftUp, 130, 0);
        await f.Mouse.Controller.WaitForWorkAsync();
        f.Mouse.Platform.Runner.TotalStartCalls.ShouldBe(reads); f.Mouse.Provider.Requests.ShouldBeEmpty();
        await lease.DisposeAsync(); window.IsVisible.ShouldBeFalse();
        await f.Mouse.ShowCandidate(); f.Mouse.Platform.Runner.TotalStartCalls.ShouldBe(reads + 1);
    });

    internal sealed class Visibility : IPreparationVisibility
    {
        internal int FailHideAt, Hides;
        internal bool FailShow, FailBeforeHide;
        internal Action? BeforeShow;
        public void Hide(Window window) { if (FailBeforeHide) throw new InvalidOperationException("private window"); window.Hide(); if (++Hides == FailHideAt) throw new InvalidOperationException("private window"); }
        public void Show(Window window) { BeforeShow?.Invoke(); if (FailShow) throw new InvalidOperationException("private window"); window.Show(); }
    }
}
