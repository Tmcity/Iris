using System.ComponentModel;
using System.Windows;
using Iris.App.Capture;
using Iris.Core.Errors;
using Shouldly;

namespace Iris.App.Tests.Capture;

public sealed class ScreenshotPreparationR1Tests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task SameOperation_InvalidatesBeforeNativePlacement(bool focus) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        var lease = await f.Preparation.PrepareAsync(true);
        window.IsVisible.ShouldBeFalse();
        bool entered = false;
        f.Mouse.Position.Result = show =>
        {
            if (show)
            {
                entered = true;
                lease.Reason.ShouldBe(PreparationRevocation.DisplayIntent);
            }
            return true;
        };
        try
        {
            (await f.Mouse.Presenter.TryShowAsync(operation, new() { Text = "new publication", FocusInput = focus })).ShouldBeTrue();
            entered.ShouldBeTrue();
            await lease.DisposeAsync(); window.IsVisible.ShouldBeTrue();
        }
        finally { f.Mouse.Position.Result = null; }
    });

    [Theory]
    [InlineData("show-hide", false)][InlineData("hide-show", true)][InlineData("show", true)]
    public Task RestoreNativeBoundary_RespectsLatestAcceptedIntent(string sequence, bool visible) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        bool originalShowActivated = window.ShowActivated;
        var native = new Visibility(); f.Preparation.Visibility = native;
        var lease = await f.Preparation.PrepareAsync(true);
        bool entered = false;
        native.BeforeShow = () =>
        {
            entered = true;
            var mutation = MutateAsync(); f.Work.Add(mutation);
            // The actual UI calls must finish inside this native boundary; never block the UI.
            mutation.IsCompletedSuccessfully.ShouldBeTrue();
            window.IsVisible.ShouldBe(visible);
        };
        async Task MutateAsync()
        {
            if (sequence == "hide-show") await f.Mouse.Presenter.HideAsync();
            using var next = sequence == "hide-show" ? f.Mouse.Presenter.TryBeginOperation()! : null;
            (await f.Mouse.Presenter.TryShowAsync(next ?? operation, new() { Text = "latest", FocusInput = false })).ShouldBeTrue();
            window.IsVisible.ShouldBeTrue();
            if (sequence == "show-hide") await f.Mouse.Presenter.HideAsync();
        }
        try
        {
            await lease.DisposeAsync(); entered.ShouldBeTrue();
            window.IsVisible.ShouldBe(visible); window.ShowActivated.ShouldBe(originalShowActivated);
        }
        finally { native.BeforeShow = null; }
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task FailedUndoRetry_UsesLatestIntentAfterFurtherShowHide(bool latestShow) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        var native = new Visibility(); f.Preparation.Visibility = native;
        var lease = await f.Preparation.PrepareAsync(true);
        native.BeforeShow = () =>
        {
            var mutation = InitialMutationAsync(); f.Work.Add(mutation);
            mutation.IsCompletedSuccessfully.ShouldBeTrue();
            window.IsVisible.ShouldBeFalse();
            native.FailHide = true;
        };
        async Task InitialMutationAsync()
        {
            (await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false })).ShouldBeTrue();
            await f.Mouse.Presenter.HideAsync();
        }
        try
        {
            var error = await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask());
            error.Detail!.ShouldContain("LATE_HIDE"); window.IsVisible.ShouldBeTrue();
            native.BeforeShow = null;
            using var next = f.Mouse.Presenter.TryBeginOperation()!;
            (await f.Mouse.Presenter.TryShowAsync(next, new() { FocusInput = false })).ShouldBeTrue();
            if (!latestShow) await f.Mouse.Presenter.HideAsync();
            native.FailHide = false;
            await f.Preparation.RetryCleanupAsync(); window.IsVisible.ShouldBe(latestShow);
        }
        finally { native.BeforeShow = null; native.FailHide = false; }
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task FailedSettingsClose_IsNotMistakenForAbsentWindow(bool hide) => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var session = f.Settings.ActiveSession!;
        session.ViewModel.Selection.ClipboardTimeoutText = "713";
        CancelEventHandler refuse = (_, e) => e.Cancel = true;
        session.Window.Closing += refuse;
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => session.ShutdownAsync());
            session.Window.IsVisible.ShouldBeTrue(); session.CurrentCleanupTask!.IsFaulted.ShouldBeTrue();
            if (hide)
            {
                var error = await Should.ThrowAsync<IrisException>(() => f.Preparation.PrepareAsync(true));
                error.Code.ShouldBe(ErrorCode.CaptureFailed);
            }
            else await using (await f.Preparation.PrepareAsync(false)) { }
            f.Settings.ActiveSession.ShouldBeSameAs(session); session.Window.IsVisible.ShouldBeTrue();
            session.ViewModel.Selection.ClipboardTimeoutText.ShouldBe("713");
        }
        finally
        {
            session.Window.Closing -= refuse;
            // On failure the owning scope retries this session and retains both primary/cleanup errors.
        }
        await session.ShutdownAsync();
        session.Window.IsVisible.ShouldBeFalse();
        await using (await f.Preparation.PrepareAsync(true)) { }
        f.Settings.ActiveSession.ShouldBeNull();
    });

    [Fact]
    public Task ClosingSettingsWithActualRecordingGate_CannotPrepareWhileVisible() => PreparationTestScope.Run(async f =>
    {
        await f.OpenSettings(); var session = f.Settings.ActiveSession!;
        var gate = f.Mouse.Gate(); f.Native.BeforeUnhook = gate.Block;
        Task? close = null, preparation = null;
        var recording = session.ViewModel.StartRecordingAsync(session.ViewModel.Hotkeys[0]);
        f.Work.Add(recording);
        try
        {
            await f.Native.Inner.PumpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            close = session.ShutdownAsync(); f.Work.Add(close);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Window.IsVisible.ShouldBeTrue(); close.IsCompleted.ShouldBeFalse();
            preparation = ObservePreparationAsync(); f.Work.Add(preparation);
            preparation.IsCompleted.ShouldBeFalse();
            gate.Release(); await close; await preparation;
            session.Window.IsVisible.ShouldBeFalse();
            await using (await f.Preparation.PrepareAsync(true)) { }
        }
        finally
        {
            f.Native.BeforeUnhook = null; gate.Release();
            // The owning scope's finally now drains preparation, recording, and all tracked work.
            // It preserves assertion failures together with any cleanup errors.
        }
        async Task ObservePreparationAsync()
        {
            try
            {
                await using var lease = await f.Preparation.PrepareAsync(true);
                session.Window.IsVisible.ShouldBeFalse();
            }
            catch (OperationCanceledException) { }
            catch (IrisException error) { error.Code.ShouldBe(ErrorCode.CaptureFailed); }
        }
    });

    private sealed class Visibility : IPreparationVisibility
    {
        internal Action? BeforeShow;
        internal bool FailHide;
        public void Hide(Window window)
        {
            if (FailHide) throw new IOException("synthetic late hide failure");
            window.Hide();
        }
        public void Show(Window window) { BeforeShow?.Invoke(); window.Show(); }
    }
}
