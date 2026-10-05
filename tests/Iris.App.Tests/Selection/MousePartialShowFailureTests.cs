using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using Iris.App.Selection;
using Iris.Core.Errors;
using Iris.Core.Settings;
using Iris.Platform.Windows.Selection;
using Shouldly;

namespace Iris.App.Tests.Selection;

public sealed class MousePartialShowFailureTests
{
    [Theory]
    [InlineData("placement-before")]
    [InlineData("show-after")]
    [InlineData("placement-after")]
    public Task FailedDisplay_IsHiddenBeforeErrorAndAfterDeadline_OldClickIsInert(string stage) => MouseInteractionTestScope.Run(async f =>
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync();
        f.Platform.UiaStatus = UiaSelectionStatus.Success;
        bool visibleAtFailure = false;
        f.Position.Result = show =>
        {
            if (stage == (show ? "placement-after" : "placement-before"))
            {
                visibleAtFailure = f.Factory.Windows.Single().IsVisible;
                return false;
            }
            return true;
        };
        f.Icon.AfterShow = window =>
        {
            if (stage == "show-after") { visibleAtFailure = window.IsVisible; throw new IOException("private show failure"); }
        };
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, MouseSelectionRuntimeSnapshot snapshot)
        { if (snapshot.Error != null) published.TrySetResult(f.Factory.Windows.Any(w => w.IsVisible)); }
        f.Controller.RuntimeChanged += Changed;
        try
        {
            await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
            bool visibleAtError = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Controller.WaitForWorkAsync();
            visibleAtFailure.ShouldBe(stage != "placement-before");
            visibleAtError.ShouldBeFalse(); f.Factory.Windows.Single().IsVisible.ShouldBeFalse();
            f.Controller.Runtime.Error.ShouldBe(ErrorCode.HookInstallFailed); f.Controller.Runtime.CanRetry.ShouldBeTrue();
            f.Platform.Time.Advance(TimeSpan.FromMilliseconds(f.Settings.Current.Selection.IconAutoHideMs + 1));
            await f.Controller.WaitForWorkAsync(); f.Factory.Windows.Single().IsVisible.ShouldBeFalse();
            f.Factory.Windows.Single().TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await f.Controller.WaitForWorkAsync(); f.Provider.Requests.ShouldBeEmpty();
            f.Platform.Runner.TotalStartCalls.ShouldBe(1);
        }
        finally { f.Controller.RuntimeChanged -= Changed; }
    });

    [Fact]
    public Task HideFailure_RetainsViewAndBothErrors_BlocksShowUntilRetry() => WithTime(async (f, timers) =>
    {
        await Start(f);
        f.Position.Result = show => !show; f.Icon.FailHide = true;
        await ProduceCandidate(f);
        var window = f.Factory.Windows.Single();
        window.IsVisible.ShouldBeTrue(); f.Controller.Runtime.CanRetry.ShouldBeTrue();
        var error = VisualError(f); error.Detail!.ShouldContain("phase=ICON_SHOW"); error.Detail!.ShouldContain("cleanup=HIDE");
        error.InnerException.ShouldBeNull(); error.ToString().ShouldNotContain("private"); error.Detail!.ShouldNotContain("private");
        timers.Deadlines.Single().IsDisposed.ShouldBeTrue();
        int hides = f.Icon.HideAttempts;
        f.Platform.Time.Advance(TimeSpan.FromMilliseconds(3001)); await f.Controller.WaitForWorkAsync();
        f.Icon.HideAttempts.ShouldBe(hides); // Neither an invalid deadline nor an automatic Show loop retries this failure.
        window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await f.Controller.WaitForWorkAsync();
        f.Provider.Requests.ShouldBeEmpty();

        f.Position.Result = null;
        await ProduceCandidate(f);
        f.Icon.Shows.ShouldBe(1); timers.Deadlines.Last().IsDisposed.ShouldBeFalse();
        f.Icon.FailHide = false; await f.Controller.RetryAsync(); await f.Controller.WaitForWorkAsync();
        window.IsVisible.ShouldBeFalse(); timers.Deadlines.ShouldAllBe(t => t.IsDisposed);
        f.Platform.UiaText = "new candidate";
        await ProduceCandidate(f); window.IsVisible.ShouldBeTrue(); f.Factory.Windows.Count.ShouldBe(1);
        window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await f.Controller.WaitForWorkAsync();
        f.Presenter.ViewModel!.InputText.ShouldBe("new candidate");
        await f.Mode(MouseSelectionMode.Off); await f.Controller.WaitForApplyAsync();
        window.IsVisible.ShouldBeFalse(); await f.Controller.StopAsync();
    });

    [Fact]
    public Task CloseFailureAfterFailedShow_RetainsOwnershipForRealStopRetry() => WithTime(async (f, timers) =>
    {
        await Start(f); f.Position.Result = show => !show; f.Icon.FailHide = true;
        await ProduceCandidate(f);
        f.Icon.FailClose = true;
        await f.Mode(MouseSelectionMode.Off); await f.Controller.WaitForApplyAsync(); await f.Controller.WaitForWorkAsync();
        VisualError(f).Detail!.ShouldContain("phase=ICON_SHOW"); VisualError(f).Detail!.ShouldContain("cleanup=CLOSE");
        (await Should.ThrowAsync<IrisException>(() => f.Controller.StopAsync())).Detail!.ShouldContain("WINDOW");
        f.Factory.Windows.Single().IsVisible.ShouldBeTrue(); timers.Deadlines.Single().IsDisposed.ShouldBeTrue();
        f.Icon.FailClose = false; f.Icon.FailHide = false; await f.Controller.StopAsync();
        f.Factory.Windows.Single().IsVisible.ShouldBeFalse(); f.Icon.Closes.ShouldBe(1);
        f.Mouse.ActiveHandles.ShouldBe(0); f.Mouse.MessageThread!.IsAlive.ShouldBeFalse();
    });

    [Fact]
    public Task TimerFailure_RetainsActualTimerForRetry() => WithTime(async (f, timers) =>
    {
        await Start(f); timers.DisposeFailures = 1; f.Position.Result = show => !show;
        await ProduceCandidate(f);
        f.Factory.Windows.Single().IsVisible.ShouldBeFalse(); f.Controller.Runtime.CanRetry.ShouldBeTrue();
        var timer = timers.Deadlines.Single(); timer.IsDisposed.ShouldBeFalse(); timer.DisposeAttempts.ShouldBe(1);
        VisualError(f).Detail!.ShouldContain("phase=ICON_SHOW"); VisualError(f).Detail!.ShouldNotContain("timer=0x00000000");
        f.Icon.Hides.ShouldBe(1);
        f.Position.Result = null;
        await f.Controller.RetryAsync(); await f.Controller.WaitForWorkAsync();
        timer.IsDisposed.ShouldBeTrue(); timer.DisposeAttempts.ShouldBe(2); f.Icon.Shows.ShouldBe(1);
        f.Controller.Runtime.Error.ShouldBeNull();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ErrorPublicationAndStop_WaitActualTimerDisposal_FinallyReleasesGate(bool injectFailure) => WithTime(async (f, timers) =>
    {
        await Start(f); f.Position.Result = show => !show;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        timers.DisposeGate = release.Task; f.Release.Add(() => release.TrySetResult());
        var injected = new InvalidOperationException("injected test failure after timer entered");
        Exception? observed = null; Task? work = null, stop = null;
        var errors = new List<Exception>();
        try
        {
            await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
            work = f.Track(f.Controller.WaitForWorkAsync());
            await timers.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Hide completed synchronously on the UI thread before waiting for the real deadline retirement.
            f.Factory.Windows.Single().IsVisible.ShouldBeFalse(); work.IsCompleted.ShouldBeFalse();
            f.Controller.Runtime.Error.ShouldBeNull(); timers.Deadlines.Single().IsDisposed.ShouldBeFalse();
            stop = f.Track(f.Controller.StopAsync()); stop.IsCompleted.ShouldBeFalse();
            if (injectFailure) throw injected;
        }
        catch (Exception ex) { observed = ex; }
        finally
        {
            release.TrySetResult();
            if (work != null) try { await work; } catch (Exception ex) { errors.Add(ex); }
            if (stop != null) try { await stop; } catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException(observed == null ? errors : new[] { observed }.Concat(errors));
        if (injectFailure) observed.ShouldBeSameAs(injected); else observed.ShouldBeNull();
        timers.Deadlines.Single().IsDisposed.ShouldBeTrue(); f.Factory.Windows.Single().IsVisible.ShouldBeFalse();
        f.Mouse.MessageThread!.IsAlive.ShouldBeFalse();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NewConfigurationDuringShow_IsNotOverwrittenByOldFailure(bool turnOff) => WithTime(async (f, timers) =>
    {
        await Start(f);
        var errors = new List<MouseSelectionRuntimeSnapshot>();
        void Changed(object? sender, MouseSelectionRuntimeSnapshot snapshot) { if (snapshot.Error != null) errors.Add(snapshot); }
        f.Controller.RuntimeChanged += Changed;
        bool invoked = false;
        f.Position.Result = show =>
        {
            if (!show || invoked) return true;
            invoked = true; f.Factory.Windows.Single().IsVisible.ShouldBeTrue();
            _ = f.Track(f.Settings.UpdateAsync(s => s with { Selection = s.Selection with
            { MouseMode = turnOff ? MouseSelectionMode.Off : MouseSelectionMode.ShowIcon, ClipboardTimeoutMs = 777 } }));
            return false;
        };
        try
        {
            await ProduceCandidate(f); await f.Controller.WaitForApplyAsync(); await f.Controller.WaitForWorkAsync();
            invoked.ShouldBeTrue(); errors.ShouldBeEmpty(); f.Controller.Runtime.Error.ShouldBeNull();
            f.Controller.Runtime.DesiredMode.ShouldBe(turnOff ? MouseSelectionMode.Off : MouseSelectionMode.ShowIcon);
            f.Factory.Windows.Single().IsVisible.ShouldBeFalse(); timers.Deadlines.Single().IsDisposed.ShouldBeTrue();
            f.Settings.Current.Selection.ClipboardTimeoutMs.ShouldBe(777);
            if (!turnOff) { await ProduceCandidate(f); f.Factory.Windows.Single().IsVisible.ShouldBeTrue(); }
        }
        finally { f.Controller.RuntimeChanged -= Changed; }
    });

    [Fact]
    public Task NewCandidateDuringFailedShow_KeepsItsTimerAndShowsOnlyAfterOldHide() => WithTime(async (f, timers) =>
    {
        await Start(f);
        var showing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = f.Gate();
        int shows = 0;
        f.Position.Result = show =>
        {
            if (!show || ++shows != 1) return true;
            f.Factory.Windows.Single().IsVisible.ShouldBeTrue(); showing.TrySetResult(); gate.Block(); return false;
        };
        var replaced = f.Track(Task.Run(async () =>
        {
            try
            {
                await showing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await f.Mouse.EnqueueAsync(Iris.Platform.Windows.Hooks.MouseMessage.LeftDown, 50, 0);
                await f.Mouse.EnqueueAsync(Iris.Platform.Windows.Hooks.MouseMessage.LeftUp, 80, 0);
                await f.Platform.Time.WaitForDelayAsync(120);
                f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
                await timers.SecondCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { gate.Release(); }
        }));
        await ProduceCandidate(f); await replaced; await f.Controller.WaitForWorkAsync();
        f.Controller.Runtime.Error.ShouldBeNull(); f.Factory.Windows.Single().IsVisible.ShouldBeTrue();
        f.Icon.Shows.ShouldBe(2); f.Icon.Hides.ShouldBe(1); f.Platform.Runner.TotalStartCalls.ShouldBe(2);
        var deadlines = timers.Deadlines.ToArray(); deadlines[0].IsDisposed.ShouldBeTrue(); deadlines[1].IsDisposed.ShouldBeFalse();
        f.Platform.Time.Advance(TimeSpan.FromMilliseconds(2999)); await f.Controller.WaitForWorkAsync();
        f.Factory.Windows.Single().IsVisible.ShouldBeTrue();
        f.Platform.Time.Advance(TimeSpan.FromMilliseconds(1)); await f.Controller.WaitForWorkAsync();
        f.Factory.Windows.Single().IsVisible.ShouldBeFalse(); deadlines[1].IsDisposed.ShouldBeTrue();
    });

    private static async Task Start(MouseInteractionTestScope f)
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync(); f.Platform.UiaStatus = UiaSelectionStatus.Success;
    }
    private static async Task ProduceCandidate(MouseInteractionTestScope f)
    {
        await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120)); await f.Controller.WaitForWorkAsync();
    }
    private static IrisException VisualError(MouseInteractionTestScope f) =>
        (IrisException)typeof(SelectionIconController).GetField("_visualFailure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Controller)!;
    private static Task WithTime(Func<MouseInteractionTestScope, IconDeadlineTimeProvider, Task> body)
    {
        IconDeadlineTimeProvider? timers = null;
        return MouseInteractionTestScope.Run(f => body(f, timers!), inner => timers = new(inner));
    }
}
