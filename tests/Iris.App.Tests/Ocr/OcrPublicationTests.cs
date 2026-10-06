using System.ComponentModel;
using Iris.App.Tests.TestSupport;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Ocr;

[Collection("AppShutdownerSharedState")]
public sealed class OcrPublicationTests
{
    [Theory][InlineData("new")][InlineData("hide")][InlineData("edit")][InlineData("cancel")]
    public Task QueuedTerminalPublication_RejectsStaleTextCopyAndFallback(string change) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        using var op = s.Presenter.TryBeginOperation(caller.Token)!;
        await s.Presenter.TryShowAsync(op, new() { Text = "before", AutoTranslate = false });
        var vm = s.Presenter.ViewModel!; ITranslateWindowOperation? newer = null;
        s.Ui.Pause();
        var work = s.Presenter.TryShowAsync(op, new() { Text = "late", AutoTranslate = false, AutoCopySource = true, NotifyOcrFallback = true });
        Task? hiding = null;
        try
        {
            await s.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "new") newer = s.Presenter.TryBeginOperation();
            else if (change == "hide") hiding = s.Presenter.HideAsync();
            else if (change == "edit") vm.InputText = "user";
            else caller.Cancel();
            s.Ui.Release(); (await work).ShouldBeFalse();
            if (hiding != null) await hiding;
            vm.InputText.ShouldBe(change == "edit" ? "user" : "before");
            s.Copies.ShouldBeEmpty(); s.Native.Messages.ShouldBeEmpty();
        }
        finally { s.Ui.Release(); await work; if (hiding != null) await hiding; newer?.Dispose(); }
    });

    [Fact]
    public Task SourceCopy_RejectsOriginalCallerBeforeForwardingCallbackRuns() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        using var op = s.Presenter.TryBeginOperation(caller.Token)!;
        await s.Presenter.TryShowAsync(op, new() { Text = "before", AutoTranslate = false });
        var vm = s.Presenter.ViewModel!;
        void Pause(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(vm.InputText)) s.Ui.Pause(); }
        vm.PropertyChanged += Pause;
        using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? canceled = null;
        using var registration = caller.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
        try
        {
            (await s.Presenter.TryShowAsync(op, new() { Text = "source", AutoTranslate = false, AutoCopySource = true })).ShouldBeTrue();
            vm.PropertyChanged -= Pause; await s.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled = Task.Run(caller.Cancel, TestContext.Current.CancellationToken);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.IsCancellationRequested.ShouldBeTrue(); op.CancellationToken.IsCancellationRequested.ShouldBeFalse();
            s.Ui.Release(); await vm.WaitForActiveWorkAsync(); s.Copies.ShouldBeEmpty();
        }
        finally { vm.PropertyChanged -= Pause; s.Ui.Release(); callbackRelease.Set(); if (canceled != null) await canceled; }
    });

    [Fact]
    public Task SourceCopies_UseExistingSingleWriteAndLatestPendingSlot() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        s.Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c =>
        { writes.Add(c.Arg<string>()); entered.TrySetResult(); return writes.Count == 1 ? release.Task : Task.CompletedTask; });
        try
        {
            using (var a = s.Presenter.TryBeginOperation(TestContext.Current.CancellationToken)!)
                await s.Presenter.TryShowAsync(a, new() { Text = "a", AutoTranslate = false, AutoCopySource = true });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var text in new[] { "b", "c" })
            {
                using var op = s.Presenter.TryBeginOperation(TestContext.Current.CancellationToken)!;
                await s.Presenter.TryShowAsync(op, new() { Text = text, AutoTranslate = false, AutoCopySource = true });
            }
            writes.ShouldBe(["a"]); release.SetResult(); await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
            writes.ShouldBe(["a", "c"]);
        }
        finally { release.TrySetResult(); }
    });

    [Theory][InlineData(null)][InlineData("")][InlineData(" \n")][InlineData("error")][InlineData("status")]
    public Task NonSuccessRequests_NeverClaimSourceOrFallback(string? text) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        using var op = s.Presenter.TryBeginOperation(TestContext.Current.CancellationToken)!;
        await s.Presenter.TryShowAsync(op, new()
        {
            Text = text, AutoTranslate = false, AutoCopySource = true, NotifyOcrFallback = true,
            Error = text == "error" ? new(ErrorCode.Unknown, "private") : null,
            StatusMessage = text == "status" ? "safe status" : null
        });
        await s.Presenter.ViewModel!.WaitForActiveWorkAsync(); s.Copies.ShouldBeEmpty(); s.Native.Messages.ShouldBeEmpty();
        await s.Presenter.TryShowAsync(op, new() { Text = "valid", AutoTranslate = false, AutoCopySource = true, NotifyOcrFallback = true });
        await s.Presenter.ViewModel.WaitForActiveWorkAsync(); s.Copies.Single().ShouldBe("valid"); s.Native.Messages.Count.ShouldBe(1);
    });

    [Fact]
    public Task UnavailableFallback_IsAbandonedAtAcceptanceAndNeverDeferred() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); s.Native.IsCreated = false;
        using var op = s.Presenter.TryBeginOperation(TestContext.Current.CancellationToken)!;
        var request = new TranslateWindowRequest { Text = "source", AutoTranslate = false, NotifyOcrFallback = true };
        await s.Presenter.TryShowAsync(op, request);
        s.Native.IsCreated = true; s.Notifications.AttachTray(s.Tray);
        await s.Presenter.TryShowAsync(op, request); s.Native.Messages.ShouldBeEmpty();
    });

    [Theory][InlineData(AutoCopyMode.None)][InlineData(AutoCopyMode.FirstResult)]
    public Task OcrAutoCopy_IsIndependentOfTranslationAutoCopySetting(AutoCopyMode translationCopy) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        s.Settings.Current.Returns(s.Settings.Current with { Ocr = new() { AutoCopyOnOcr = true }, Translation = new() { AutoCopy = translationCopy } });
        await s.Run(IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
        await s.Presenter.ViewModel!.WaitForActiveWorkAsync(); s.Copies.Count.ShouldBe(1); s.Translations.ShouldBeEmpty();
        await s.Run(IrisAction.ScreenshotTranslate, TestContext.Current.CancellationToken);
        await s.Presenter.ViewModel.WaitForActiveWorkAsync(); s.Copies.Count.ShouldBe(1); s.Translations.Count.ShouldBe(1);
        s.Settings.Current.Translation.AutoCopy.ShouldBe(translationCopy);
    });

    [Fact]
    public Task SilentWithoutNotification_StillCopies_AndCaptureCancelNeverCreatesWindow() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        s.Settings.Current.Returns(s.Settings.Current with { Ocr = new() { SilentOcrShowNotification = false } });
        await s.Run(IrisAction.SilentScreenshotOcr, TestContext.Current.CancellationToken);
        s.Presenter.Window.ShouldBeNull(); s.Native.Messages.ShouldBeEmpty(); s.Copies.Count.ShouldBe(1);
        s.Screenshot.CaptureRegionAsync(Arg.Any<ScreenshotPurpose>(), Arg.Any<CancellationToken>()).Returns((ScreenshotResult?)null);
        await s.Run(IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
        s.Presenter.Window.ShouldBeNull();
    });
}
