using System.Windows;
using Iris.App.Capture;
using Iris.Core.Errors;
using Shouldly;

namespace Iris.App.Tests.Capture;

public sealed class ScreenshotPreparationR2Tests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task SuccessfulShowThenPendingOperation_RetainsDisplayUnlessExplicitlyHidden(bool hide) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        var native = new Visibility(); f.Preparation.Visibility = native;
        var lease = await f.Preparation.PrepareAsync(true);
        bool entered = false;
        native.BeforeShow = () =>
        {
            entered = true;
            var change = ChangeAsync(); f.Work.Add(change);
            change.IsCompletedSuccessfully.ShouldBeTrue();
            window.IsVisible.ShouldBe(!hide);
        };
        async Task ChangeAsync()
        {
            (await f.Mouse.Presenter.TryShowAsync(operation, new() { Text = "new display", FocusInput = false })).ShouldBeTrue();
            window.IsVisible.ShouldBeTrue();
            using var pending = f.Mouse.Presenter.TryBeginOperation();
            pending.ShouldNotBeNull();
            if (hide) await f.Mouse.Presenter.HideAsync();
        }
        try
        {
            await lease.DisposeAsync(); entered.ShouldBeTrue();
            lease.Reason.ShouldBe(PreparationRevocation.DisplayIntent);
            window.IsVisible.ShouldBe(!hide);
        }
        finally { native.BeforeShow = null; }
    });

    [Fact]
    public Task FailedNewPlacement_CannotBeResurrectedByOldRestore() => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        var native = new Visibility(); f.Preparation.Visibility = native;
        var lease = await f.Preparation.PrepareAsync(true);
        bool entered = false;
        native.BeforeShow = () =>
        {
            entered = true;
            var change = ChangeAsync(); f.Work.Add(change);
            change.IsCompletedSuccessfully.ShouldBeTrue();
        };
        async Task ChangeAsync()
        {
            f.Mouse.Position.Result = _ => false;
            try
            {
                (await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false })).ShouldBeFalse();
                window.IsVisible.ShouldBeFalse();
                lease.Reason.ShouldBe(PreparationRevocation.DisplayIntent);
            }
            finally { f.Mouse.Position.Result = null; }
        }
        try
        {
            await lease.DisposeAsync(); entered.ShouldBeTrue(); window.IsVisible.ShouldBeFalse();
        }
        finally { native.BeforeShow = null; f.Mouse.Position.Result = null; }
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task OlderPlacementFailure_CannotClearNewerSuccessfulShow(bool hideNewer) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        var native = new Visibility(); f.Preparation.Visibility = native;
        var lease = await f.Preparation.PrepareAsync(true);
        bool entered = false, nested = false;
        native.BeforeShow = () =>
        {
            entered = true;
            f.Mouse.Position.Result = _ =>
            {
                if (nested) return true;
                nested = true;
                var newer = NewerAsync(); f.Work.Add(newer);
                newer.IsCompletedSuccessfully.ShouldBeTrue();
                return false; // Only the older placement fails, after the newer owner finished.
            };
            var older = OlderAsync(); f.Work.Add(older);
            older.IsCompletedSuccessfully.ShouldBeTrue();
        };
        async Task NewerAsync()
        {
            using var next = f.Mouse.Presenter.TryBeginOperation()!;
            (await f.Mouse.Presenter.TryShowAsync(next, new() { Text = "newer owner", FocusInput = false })).ShouldBeTrue();
            window.IsVisible.ShouldBeTrue();
            if (hideNewer) await f.Mouse.Presenter.HideAsync();
        }
        async Task OlderAsync()
        {
            (await f.Mouse.Presenter.TryShowAsync(operation, new() { Text = "older rejected", FocusInput = false })).ShouldBeFalse();
            window.IsVisible.ShouldBe(!hideNewer);
        }
        try
        {
            await lease.DisposeAsync(); entered.ShouldBeTrue(); nested.ShouldBeTrue();
            window.IsVisible.ShouldBe(!hideNewer);
            f.Mouse.Presenter.ViewModel!.InputText.ShouldBe("newer owner");
        }
        finally { native.BeforeShow = null; f.Mouse.Position.Result = null; }
    });

    [Theory]
    [InlineData("pending", true)][InlineData("hidden", false)][InlineData("failed", false)]
    public Task FailedUndoRetry_UsesSettledVisibilityInsteadOfIntentVersion(string outcome, bool visible) => PreparationTestScope.Run(async f =>
    {
        using var operation = f.Mouse.Presenter.TryBeginOperation()!;
        await f.Mouse.Presenter.TryShowAsync(operation, new() { FocusInput = false });
        var window = f.Mouse.Presenter.Window!;
        var native = new Visibility(); f.Preparation.Visibility = native;
        var lease = await f.Preparation.PrepareAsync(true);
        native.BeforeShow = () =>
        {
            using var pending = f.Mouse.Presenter.TryBeginOperation();
            native.FailHide = true;
        };
        try
        {
            (await Should.ThrowAsync<IrisException>(() => lease.DisposeAsync().AsTask())).Detail!.ShouldContain("LATE_HIDE");
            native.BeforeShow = null;
            // Start from actual hidden state; a failed publish then has no prior visible display to preserve.
            await f.Mouse.Presenter.HideAsync();
            using var next = f.Mouse.Presenter.TryBeginOperation()!;
            if (outcome == "failed")
            {
                f.Mouse.Position.Result = _ => false;
                (await f.Mouse.Presenter.TryShowAsync(next, new() { FocusInput = false })).ShouldBeFalse();
                window.IsVisible.ShouldBeFalse();
            }
            else
            {
                (await f.Mouse.Presenter.TryShowAsync(next, new() { FocusInput = false })).ShouldBeTrue();
                using var pending = f.Mouse.Presenter.TryBeginOperation();
                if (outcome == "hidden") await f.Mouse.Presenter.HideAsync();
            }
            native.FailHide = false;
            await f.Preparation.RetryCleanupAsync(); window.IsVisible.ShouldBe(visible);
        }
        finally { native.BeforeShow = null; native.FailHide = false; f.Mouse.Position.Result = null; }
    });

    private sealed class Visibility : IPreparationVisibility
    {
        internal Action? BeforeShow;
        internal bool FailHide;
        public void Hide(Window window)
        {
            if (FailHide) throw new IOException("synthetic undo failure");
            window.Hide();
        }
        public void Show(Window window) { BeforeShow?.Invoke(); window.Show(); }
    }
}
