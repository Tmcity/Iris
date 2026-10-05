using System.Windows;
using System.Windows.Controls;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Platform.Windows.Hooks;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Selection;

public sealed class MouseInteractionTests
{
    [Fact]
    public Task Off_IsLazyAndDoesNotInstallReadOrCreateWindow() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Controller.StartAsync();
        f.Mouse.Calls.ShouldBeEmpty(); f.Platform.Runner.TotalStartCalls.ShouldBe(0); f.Factory.Windows.ShouldBeEmpty();
        f.Controller.Runtime.DesiredMode.ShouldBe(MouseSelectionMode.Off);
    });

    [Fact]
    public Task ShowIcon_PreReadsWithoutCancellingActualTranslation_ClickUsesCacheOnce() => MouseInteractionTestScope.Run(async f =>
    {
        f.Provider.HoldFirst = true;
        f.Provider.HoldAll = true;
        using (var operation = f.Presenter.TryBeginOperation()!)
            (await f.Presenter.TryShowAsync(operation, new() { Text = "existing translation", AutoTranslate = true, Origin = TextOrigin.Input, FocusInput = false })).ShouldBeTrue();
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync();
        await f.ShowCandidate(clipboard: true);
        f.Presenter.ViewModel!.InputText.ShouldBe("existing translation"); f.Provider.Cancellations.ShouldBe(0); f.Provider.Requests.Count.ShouldBe(1);
        f.Platform.Copies.ShouldBe(1); f.Platform.Runner.TotalStartCalls.ShouldBe(0);
        var bounds = f.Position.Calls.Last(call => call.Show).Bounds;
        bounds.Width.ShouldBe(36); bounds.Height.ShouldBe(36);
        f.Icon.Style.ShouldBe(0x08000080);
        var input = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Monitor.MouseDown += (_, _) => input.TrySetResult();
        f.Mouse.CursorHandle = 901;
        await f.Track(f.Mouse.EnqueueAsync(MouseMessage.LeftDown, bounds.X + 1, bounds.Y + 1)); await input.Task;
        var window = f.Factory.Windows.Single();
        window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await f.Dispatcher.WaitForInFlightAsync();
        // The Controller dispatch may still be queued behind its completed passive worker.
        await f.Controller.WaitForWorkAsync();
        await f.Provider.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Platform.Copies.ShouldBe(1); f.Platform.Runner.TotalStartCalls.ShouldBe(0);
        f.Presenter.ViewModel!.InputText.ShouldBe(" cached original\r\ntext ");
        await f.Mode(MouseSelectionMode.Off); await f.Controller.WaitForApplyAsync();
        f.Presenter.IsVisible.ShouldBeTrue();
        f.Provider.Tokens.First().IsCancellationRequested.ShouldBeTrue();
        f.Provider.Tokens.Last().IsCancellationRequested.ShouldBeFalse(); // Never cancel the adopted second translation.
    });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public Task Auto_UsesOneRealReadAndEmptyDoesNotOpenWindow(bool empty) => MouseInteractionTestScope.Run(async f =>
    {
        f.Platform.UiaStatus = empty ? Iris.Platform.Windows.Selection.UiaSelectionStatus.Empty : Iris.Platform.Windows.Selection.UiaSelectionStatus.Success;
        await f.Mode(MouseSelectionMode.AutoTranslate); await f.Controller.StartAsync();
        await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
        await f.Controller.WaitForWorkAsync();
        if (empty)
        {
            await f.Controller.StopAsync();
            f.Presenter.IsVisible.ShouldBeFalse();
        }
        else f.Presenter.ViewModel!.InputText.ShouldBe("uia text");
        f.Platform.Runner.TotalStartCalls.ShouldBe(1);
        f.Factory.Windows.ShouldBeEmpty();
    });

    [Fact]
    public Task OutsideInputAndExpiry_ClearOnlyOwnIcon() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync(); await f.ShowCandidate();
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Monitor.MouseDown += (_, _) => hidden.TrySetResult();
        await f.Track(f.Mouse.EnqueueAsync(MouseMessage.RightDown, -900, -400)); await hidden.Task;
        await MouseInteractionTestScope.FlushUi();
        f.Factory.Windows[0].TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        f.Provider.Requests.ShouldBeEmpty();
        await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
        await f.Controller.WaitForWorkAsync();
        f.Platform.Time.Advance(TimeSpan.FromMilliseconds(3000)); await f.Controller.WaitForWorkAsync();
        f.Factory.Windows[0].TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        f.Provider.Requests.ShouldBeEmpty();
    });

    [Fact]
    public Task LatestGesture_WaitsOldActualClipboardRestore() => MouseInteractionTestScope.Run(async f =>
    {
        f.Platform.Seed();
        await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { MouseMode = MouseSelectionMode.ShowIcon, ReadOrder = [SelectionReadMethod.Clipboard] } });
        await f.Controller.StartAsync(); await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
        await f.Platform.Time.WaitForDelayAsync(15);
        var restore = f.Platform.NativeGate("Set");
        await f.Track(f.Mouse.EnqueueAsync(MouseMessage.LeftDown, 30, 0)); await f.Track(f.Mouse.EnqueueAsync(MouseMessage.LeftUp, 60, 0));
        await restore.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        f.Platform.Copies.ShouldBe(1); f.Factory.Windows.ShouldBeEmpty();
        var stop = f.Track(f.Controller.StopAsync()); stop.IsCompleted.ShouldBeFalse();
        restore.Release.Set(); await stop; f.Platform.ClipboardText().ShouldBe("old text"); f.Platform.Copies.ShouldBe(1);
    });

    [Fact]
    public Task InstallFailureAndLateStop_RetainSavedModeAndRetrySameMonitor() => MouseInteractionTestScope.Run(async f =>
    {
        f.Mouse.InstallFailure = true;
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync();
        f.Settings.Current.Selection.MouseMode.ShouldBe(MouseSelectionMode.ShowIcon); f.Controller.Runtime.CanRetry.ShouldBeTrue();
        f.Mouse.InstallFailure = false; await f.Controller.RetryAsync(); f.Monitor.IsRunning.ShouldBeTrue();
        var original = f.Mouse.MessageThread!;
        f.Mouse.InstallGate = f.Gate();
        await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { MinDragDistancePx = 12 } });
        await f.Mouse.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = f.Track(f.Controller.StopAsync()); stop.IsCompleted.ShouldBeFalse();
        f.Mouse.InstallGate.Release(); await stop;
        original.IsAlive.ShouldBeFalse(); f.Mouse.MessageThread!.IsAlive.ShouldBeFalse();
    });

    [Fact]
    public Task CloseFailure_RetainsWindowForRetry_AndReadOnlyConfigDoesNotReinstall() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync(); await f.ShowCandidate();
        int installs = f.Mouse.Calls.Count(c => c.Name == nameof(f.Mouse.SetHook));
        await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { ClipboardTimeoutMs = 999 } }); await f.Controller.WaitForApplyAsync();
        f.Mouse.Calls.Count(c => c.Name == nameof(f.Mouse.SetHook)).ShouldBe(installs);
        f.Icon.FailClose = true; await f.Mode(MouseSelectionMode.Off); await f.Controller.WaitForApplyAsync();
        f.Controller.Runtime.CanRetry.ShouldBeTrue(); f.Factory.Windows.Count.ShouldBe(1);
        f.Icon.FailClose = false; await f.Controller.RetryAsync(); f.Icon.Closes.ShouldBe(1);
    });

    [Fact]
    public Task UiSourceMismatchAndOldClick_NeverReviveCandidate() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync();
        f.Platform.UiaStatus = Iris.Platform.Windows.Selection.UiaSelectionStatus.Success; f.Icon.Foreground = 99;
        await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120)); await f.Controller.WaitForWorkAsync();
        f.Factory.Windows.ShouldBeEmpty();
        f.Icon.Foreground = 0x1234;
        await f.Settings.UpdateAsync(s => s with { General = s.General with { ShowStartupNotification = false } });
        await f.Controller.WaitForWorkAsync(); f.Factory.Windows.ShouldBeEmpty();
        await f.ShowCandidate(); var old = f.Factory.Windows.Single();
        await f.Mode(MouseSelectionMode.Off); await f.Controller.WaitForApplyAsync();
        old.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await f.Controller.WaitForWorkAsync();
        f.Provider.Requests.ShouldBeEmpty();
    });

    [Fact]
    public Task ConfigurationBurst_KeepsOnlyLatestAfterLateInstall() => MouseInteractionTestScope.Run(async f =>
    {
        f.Mouse.InstallGate = f.Gate(); await f.Mode(MouseSelectionMode.ShowIcon);
        var start = f.Track(f.Controller.StartAsync());
        await f.Mouse.InstallGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { MinDragDistancePx = 8 } });
        await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { MinDragDistancePx = 9 } });
        await f.Mode(MouseSelectionMode.AutoTranslate);
        start.IsCompleted.ShouldBeFalse(); f.Mouse.ActiveHandles.ShouldBe(0);
        f.Mouse.InstallGate.Release(); await start;
        f.Controller.Runtime.ActualMode.ShouldBe(MouseSelectionMode.AutoTranslate);
        f.Mouse.Calls.Count(c => c.Name == nameof(f.Mouse.SetHook)).ShouldBe(2); f.Mouse.ActiveHandles.ShouldBe(1);
    });

    [Fact]
    public Task StopFailures_RetainMonitorAndWindowErrorsForRetry() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync(); await f.ShowCandidate();
        f.Mouse.UnhookFailures = int.MaxValue; f.Icon.FailClose = true;
        var error = await Should.ThrowAsync<IrisException>(() => f.Controller.StopAsync());
        error.Detail!.ShouldContain("MONITOR"); error.Detail!.ShouldContain("WINDOW");
        f.Mouse.ActiveHandles.ShouldBe(1); f.Icon.Closes.ShouldBe(0);
        f.Mouse.UnhookFailures = 0; f.Icon.FailClose = false; await f.Controller.StopAsync();
        f.Mouse.ActiveHandles.ShouldBe(0); f.Icon.Closes.ShouldBe(1);
    });

    [Fact]
    public Task NewestPendingGesture_OnlyExecutesAfterOldRestoreAndSupersedesIntermediateInputs() => MouseInteractionTestScope.Run(async f =>
    {
        f.Platform.Seed();
        await f.Settings.UpdateAsync(s => s with { Selection = s.Selection with { MouseMode = MouseSelectionMode.ShowIcon, ReadOrder = [SelectionReadMethod.Clipboard] } });
        await f.Controller.StartAsync(); await f.Drag(); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120));
        await f.Platform.Time.WaitForDelayAsync(15); var restore = f.Platform.NativeGate("Set");
        for (int x = 30; x <= 90; x += 30)
        {
            await f.Track(f.Mouse.EnqueueAsync(MouseMessage.LeftDown, x, 0)); await f.Track(f.Mouse.EnqueueAsync(MouseMessage.LeftUp, x + 20, 0));
        }
        await restore.Entered.WaitAsync(TimeSpan.FromSeconds(10)); f.Platform.Copies.ShouldBe(1);
        f.Platform.CopyOnSend("latest"); restore.Release.Set();
        await f.Platform.Time.WaitForDelayAsync(120); f.Platform.Time.Advance(TimeSpan.FromMilliseconds(120)); await f.Platform.Tick(20);
        await f.Controller.WaitForWorkAsync();
        f.Platform.Copies.ShouldBe(2); f.Position.Calls.Last(c => c.Show).Bounds.X.ShouldBe(128);
    });

    [Fact]
    public Task CachedClick_AnchorsActualTranslationAtIconPosition() => MouseInteractionTestScope.Run(async f =>
    {
        await f.Mode(MouseSelectionMode.ShowIcon); await f.Controller.StartAsync(); await f.ShowCandidate();
        var icon = f.Position.Calls.Last(c => c.Show).Bounds;
        f.Platform.Desktop.GetMonitors().Returns([new MonitorInfo("fake", new(-1000, -500, 5000, 3000), new(-1000, -500, 5000, 3000), 1.5, true)]);
        f.Factory.Windows.Single().TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await f.Controller.WaitForWorkAsync();
        var translation = f.Position.Calls.Last(c => c.Show && c.Bounds.Width > 36);
        translation.Bounds.X.ShouldBe(icon.X + Iris.App.Translation.WindowPlacementHelper.DefaultCursorOffsetPx);
        translation.Bounds.Y.ShouldBe(icon.Y + Iris.App.Translation.WindowPlacementHelper.DefaultCursorOffsetPx);
        f.Platform.Runner.TotalStartCalls.ShouldBe(1);
    });

}
