using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Hosting;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Platform.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Ocr;

[Collection("AppShutdownerSharedState")]
public sealed class OcrEntrypointTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public Task RealWindow_OcrFillsSource_TranslationOnlyWhenRequested(bool translate) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        await s.Run(translate ? IrisAction.ScreenshotTranslate : IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
        var window = s.Presenter.Window; window.ShouldNotBeNull();
        s.Presenter.ViewModel!.InputText.ShouldBe(OcrEntrypointScope.Outcome().Text);
        ((TextBox)window.FindName("InputTextBox")).Text.ShouldBe(OcrEntrypointScope.Outcome().Text);
        await s.Presenter.ViewModel.WaitForActiveWorkAsync();
        s.Translations.Count.ShouldBe(translate ? 1 : 0);
        if (translate) s.Translations.Single().Origin.ShouldBe(TextOrigin.Screenshot);
        await s.Run(IrisAction.ScreenshotOcr, TestContext.Current.CancellationToken);
        s.Presenter.Window.ShouldBeSameAs(window);
        s.Copies.ShouldBeEmpty();
        var translateButton = (System.Windows.Controls.Primitives.ButtonBase)window.FindName("TranslateButton");
        translateButton.IsEnabled.ShouldBeTrue();
        if (!translate)
        {
            translateButton.Command.Execute(translateButton.CommandParameter);
            await s.Presenter.ViewModel.WaitForActiveWorkAsync();
            s.Translations.Count.ShouldBe(1);
        }
    });

    [Fact]
    public Task SilentOcr_DoesNotReplaceOrCancelExistingTranslation() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        using var operation = s.Presenter.TryBeginOperation(TestContext.Current.CancellationToken)!;
        await s.Presenter.TryShowAsync(operation, new() { Text = "existing", AutoTranslate = false });
        var window = s.Presenter.Window;
        await s.Run(IrisAction.SilentScreenshotOcr, TestContext.Current.CancellationToken);
        s.Presenter.Window.ShouldBeSameAs(window); s.Presenter.ViewModel!.InputText.ShouldBe("existing");
        operation.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        s.Copies.Single().ShouldBe(OcrEntrypointScope.Outcome().Text);
        s.Native.Messages.Single().Text.ShouldBe($"已复制 {OcrEntrypointScope.Outcome().Text.Length} 个字符");
    });

    [Fact]
    public Task CallerAlreadyCanceled_BeforeForwardingCallback_RejectsPublicationAndNotification() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        using var operation = s.Presenter.TryBeginOperation(caller.Token)!;
        await s.Presenter.TryShowAsync(operation, new() { Text = "before", AutoTranslate = false });
        Task<bool>? publication = null, notification = null;
        using var laterRegistration = caller.Token.Register(() =>
        {
            publication = s.Presenter.TryShowAsync(operation, new() { Text = "LATE_AFTER_CALLER_CANCEL", AutoTranslate = false, AutoCopySource = true, NotifyOcrFallback = true });
            notification = s.Presenter.TryNotifyAsync(operation, NotificationLevel.Error, ErrorCode.Unknown);
        });
        caller.Cancel();
        (await publication!).ShouldBeFalse(); (await notification!).ShouldBeFalse();
        s.Presenter.ViewModel!.InputText.ShouldBe("before"); s.Copies.ShouldBeEmpty(); s.Native.Messages.ShouldBeEmpty();
    });

    [Theory][InlineData("cancel")][InlineData("new")][InlineData("hide")][InlineData("edit")][InlineData("stop")]
    public Task ReentrantPropertyNotification_RejectsOcrSideEffects(string change) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        using var op = s.Presenter.TryBeginOperation(caller.Token)!;
        await s.Presenter.TryShowAsync(op, new() { AutoTranslate = false, Text = "before" });
        ITranslateWindowOperation? newer = null; Task? hiding = null;
        var vm = s.Presenter.ViewModel!;
        void Reenter(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(vm.InputText)) return;
            vm.PropertyChanged -= Reenter;
            if (change == "cancel") caller.Cancel();
            else if (change == "new") newer = s.Presenter.TryBeginOperation();
            else if (change == "hide") hiding = s.Presenter.HideAsync();
            else if (change == "edit") vm.InputText = "user edit";
            else s.Presenter.StopAcceptance();
        }
        vm.PropertyChanged += Reenter;
        try
        {
            (await s.Presenter.TryShowAsync(op, new() { Text = "ocr", AutoTranslate = false, AutoCopySource = true, NotifyOcrFallback = true })).ShouldBeFalse();
            if (hiding != null) await hiding;
            await vm.WaitForActiveWorkAsync(); s.Copies.ShouldBeEmpty(); s.Native.Messages.ShouldBeEmpty();
        }
        finally { vm.PropertyChanged -= Reenter; newer?.Dispose(); }
    });

    [Theory][InlineData("cancel")][InlineData("new")][InlineData("hide")][InlineData("edit")][InlineData("stop")]
    public Task QueuedSourceCopy_RechecksUiIdentityCallerVisibilityAndText(string change) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); using var caller = new CancellationTokenSource();
        using var op = s.Presenter.TryBeginOperation(caller.Token)!;
        await s.Presenter.TryShowAsync(op, new() { AutoTranslate = false, Text = "before" });
        var vm = s.Presenter.ViewModel!; ITranslateWindowOperation? newer = null;
        void PauseCopy(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(vm.InputText)) s.Ui.Pause(); }
        vm.PropertyChanged += PauseCopy;
        Task? hiding = null;
        try
        {
            (await s.Presenter.TryShowAsync(op, new() { Text = "ocr", AutoTranslate = false, AutoCopySource = true })).ShouldBeTrue();
            vm.PropertyChanged -= PauseCopy;
            await s.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "cancel") caller.Cancel();
            else if (change == "new") newer = s.Presenter.TryBeginOperation();
            else if (change == "hide") hiding = s.Presenter.HideAsync();
            else if (change == "edit") vm.InputText = "edited";
            else s.Presenter.StopAcceptance();
            s.Ui.Release(); if (hiding != null) await hiding;
            await vm.WaitForActiveWorkAsync(); s.Copies.ShouldBeEmpty();
        }
        finally { vm.PropertyChanged -= PauseCopy; s.Ui.Release(); newer?.Dispose(); if (hiding != null) await hiding; }
    });

    [Fact]
    public Task SourceCopyAndFallback_AreClaimedOnceAndCopySurvivesCallerLeaseReturn() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actualToken = default; int writes = 0;
        s.Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c =>
        { writes++; actualToken = c.Arg<CancellationToken>(); entered.TrySetResult(); return release.Task; });
        var op = s.Presenter.TryBeginOperation(caller.Token)!;
        var request = new TranslateWindowRequest { Text = "source", AutoTranslate = false, AutoCopySource = true, NotifyOcrFallback = true };
        Task? shutdown = null;
        try
        {
            (await s.Presenter.TryShowAsync(op, request)).ShouldBeTrue();
            (await s.Presenter.TryShowAsync(op, request)).ShouldBeTrue();
            op.Dispose(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writes.ShouldBe(1); actualToken.IsCancellationRequested.ShouldBeFalse();
            s.Native.Messages.Single().Text.ShouldBe("已使用备用 OCR");
            var copyCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var copiedRegistration = actualToken.Register(() => copyCanceled.TrySetResult());
            caller.Cancel(); await copyCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            actualToken.IsCancellationRequested.ShouldBeTrue();
            shutdown = s.Presenter.ShutdownAsync(TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken); shutdown.IsCompleted.ShouldBeFalse();
            release.TrySetResult(); await shutdown; writes.ShouldBe(1);
        }
        finally { op.Dispose(); release.TrySetResult(); if (shutdown != null) await shutdown; }
    });

    [Fact]
    public Task CopyFailure_IsSafeVisibleAndDoesNotRewriteOnRepeatOrCleanup() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); int writes = 0;
        s.Clipboard.SetTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ =>
        { writes++; throw new IrisException(ErrorCode.ClipboardBusy, "private clipboard", detail: "private detail"); });
        using var op = s.Presenter.TryBeginOperation(TestContext.Current.CancellationToken)!;
        var request = new TranslateWindowRequest { Text = "keep source", AutoTranslate = false, AutoCopySource = true };
        await s.Presenter.TryShowAsync(op, request); await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
        s.Presenter.ViewModel.InputText.ShouldBe("keep source");
        s.Presenter.ViewModel.Error.ShouldBe(new ErrorInfo(ErrorCode.ClipboardBusy, ErrorMessages.GetDefault(ErrorCode.ClipboardBusy)));
        await s.Presenter.TryShowAsync(op, request); await s.Presenter.ViewModel.WaitForActiveWorkAsync(); writes.ShouldBe(1);
    });

    [Theory][InlineData(IrisAction.ScreenshotTranslate)][InlineData(IrisAction.ScreenshotOcr)][InlineData(IrisAction.SilentScreenshotOcr)]
    public Task RealTrayMenus_DispatchThreeActualHandlers(IrisAction action) => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope();
        typeof(Iris.App.Tray.TrayIconController).GetMethod("CreateContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s.Tray, null);
        var item = action switch { IrisAction.ScreenshotTranslate => s.Tray.ScreenshotTranslateMenuItem, IrisAction.ScreenshotOcr => s.Tray.ScreenshotOcrMenuItem, _ => s.Tray.SilentScreenshotOcrMenuItem };
        item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await s.Tray.LastScreenshotTask!;
        s.Captures.Single().ShouldBe(action switch { IrisAction.ScreenshotTranslate => ScreenshotPurpose.Translate, IrisAction.ScreenshotOcr => ScreenshotPurpose.Ocr, _ => ScreenshotPurpose.SilentOcr });
    });

    [Fact]
    public Task ExistingHotkeyConfiguration_ReachesActualHandlersAndLeavesSilentDefaultDisabled() => WpfTestRunner.RunAsync(async () =>
    {
        await using var s = new OcrEntrypointScope(); var native = new FakeNativeHotkeyAdapter();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, native);
        using var hotkeys = new HotkeyCoordinator(s.Settings, service, s.Dispatcher, s.Handlers, s.Ui, s.Notifications, NullLogger<HotkeyCoordinator>.Instance);
        try
        {
            await hotkeys.StartAsync();
            foreach (var action in new[] { IrisAction.ScreenshotTranslate, IrisAction.ScreenshotOcr })
            {
                var binding = s.Settings.Current.Hotkeys[action];
                var reg = native.RegisteredHotkeys.Single(r => r.Vk == binding.Key[0]);
                native.SendWmHotkey(reg.Id, reg.Modifiers, reg.Vk);
                for (int i = 0; i < 100 && s.Captures.Count < (action == IrisAction.ScreenshotTranslate ? 1 : 2); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
                await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
            }
            s.Settings.Current.Hotkeys[IrisAction.SilentScreenshotOcr].Enabled.ShouldBeFalse();
            s.Captures.ToArray().ShouldBe([ScreenshotPurpose.Translate, ScreenshotPurpose.Ocr]);
            var old = s.Settings.Current;
            var bindings = old.Hotkeys.ToDictionary();
            bindings[IrisAction.SilentScreenshotOcr] = bindings[IrisAction.SilentScreenshotOcr] with { Enabled = true };
            var updated = old with { Hotkeys = bindings };
            s.Settings.Current.Returns(updated);
            s.Settings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(s.Settings, new SettingsChangedEventArgs(old, updated));
            uint modifiers = (uint)bindings[IrisAction.SilentScreenshotOcr].Modifiers | 0x4000u;
            for (int i = 0; i < 100 && !native.RegisteredHotkeys.Any(r => r.Vk == 'O' && r.Modifiers == modifiers); i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            var silent = native.RegisteredHotkeys.Single(r => r.Vk == 'O' && r.Modifiers == modifiers);
            native.SendWmHotkey(silent.Id, silent.Modifiers, silent.Vk);
            for (int i = 0; i < 100 && s.Captures.Count < 3; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
            s.Captures.Last().ShouldBe(ScreenshotPurpose.SilentOcr);
        }
        finally { await hotkeys.StopAcceptanceAndNativeAsync(); await hotkeys.WaitForInFlightDispatchesAsync(); }
    });
}
