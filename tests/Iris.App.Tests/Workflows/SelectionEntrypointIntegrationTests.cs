using System.IO;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Selection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Workflows;

public sealed class SelectionEntrypointIntegrationTests
{
    [Theory]
    [InlineData("success", "new")]
    [InlineData("emptyNotify", "new")]
    [InlineData("emptyWindow", "new")]
    [InlineData("error", "new")]
    [InlineData("success", "hide")]
    [InlineData("emptyNotify", "hide")]
    [InlineData("emptyWindow", "hide")]
    [InlineData("error", "hide")]
    [InlineData("success", "cancel")]
    [InlineData("emptyNotify", "cancel")]
    [InlineData("emptyWindow", "cancel")]
    [InlineData("error", "cancel")]
    public Task LateReaderResults_AreRejectedByActualPresenter(string outcome, string invalidation) => SelectionEntryTestScope.Run(async s =>
    {
        s.Current = s.Current with { Selection = s.Current.Selection with { OpenInputWhenEmpty = outcome == "emptyWindow" } };
        var initial = s.Begin();
        (await s.Presenter.TryShowAsync(initial, new TranslateWindowRequest { Text = "existing", AutoTranslate = false })).ShouldBeTrue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); s.Release.Add(() => release.TrySetResult());
        s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            entered.TrySetResult(); await release.Task;
            if (outcome == "error") throw new IrisException(ErrorCode.CaptureFailed, "private reader error", detail: "private detail");
            return outcome == "success" ? new SelectionReadResult("late text", SelectionReadMethod.Clipboard, null) : null;
        });
        using var cts = new CancellationTokenSource();
        var read = s.Track(s.Dispatcher.DispatchAsync(IrisAction.SelectionTranslate, new ActionContext(ActionTrigger.Hotkey), cts.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (invalidation == "new")
            (await s.Presenter.TryShowAsync(s.Begin(), new TranslateWindowRequest { Text = "new", AutoTranslate = false })).ShouldBeTrue();
        if (invalidation == "hide") await s.Presenter.HideAsync();
        if (invalidation == "cancel") cts.Cancel();
        release.TrySetResult(); await read;
        s.Presenter.ViewModel!.InputText.ShouldBe(invalidation == "new" ? "new" : "existing");
        s.Presenter.IsVisible.ShouldBe(invalidation != "hide"); s.Presenter.ViewModel.Error.ShouldBeNull(); s.Native.Sent.ShouldBeEmpty();
    });

    [Fact]
    public Task Dispatcher_HoldsSameActionForEntireRead_AllowsDifferentIntent() => SelectionEntryTestScope.Run(async s =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); s.Release.Add(() => release.TrySetResult());
        int reads = 0;
        s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        { Interlocked.Increment(ref reads); entered.TrySetResult(); await release.Task; return (SelectionReadResult?)new SelectionReadResult("late", SelectionReadMethod.Clipboard, null); });
        var first = s.Track(s.Dispatcher.DispatchAsync(IrisAction.SelectionTranslate, new ActionContext(ActionTrigger.Hotkey)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await s.Dispatcher.DispatchAsync(IrisAction.SelectionTranslate, new ActionContext(ActionTrigger.Hotkey)); reads.ShouldBe(1);
        await s.Dispatcher.DispatchAsync(IrisAction.TranslateText, new ActionContext(ActionTrigger.InApp, Payload: "different action"));
        s.Presenter.ViewModel!.InputText.ShouldBe("different action"); first.IsCompleted.ShouldBeFalse();
        release.TrySetResult(); await first;
        s.Presenter.ViewModel.InputText.ShouldBe("different action");
    });

    [Fact]
    public Task DefaultHotkey_RoutesThroughRealDispatcherHandlerPresenterAndPreprocessor() => SelectionEntryTestScope.Run(async s =>
    {
        const string raw = "Hello\r\nworld";
        s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<SelectionReadResult?>(new(raw, SelectionReadMethod.UIAutomation, null)));
        var handlers = s.Provider.GetServices<IActionHandler>().ToArray();
        handlers.OfType<SelectionTranslateHandler>().Count().ShouldBe(1);
        s.Provider.GetRequiredService<ITranslateWindowPresenter>().ShouldBeSameAs(s.Presenter);
        s.Provider.GetRequiredService<ISelectionTextReader>().ShouldBeSameAs(s.Reader);
        await s.Hotkeys.StartAsync();
        var binding = s.Current.Hotkeys[IrisAction.SelectionTranslate];
        binding.Key.ShouldBe("D"); binding.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        var registered = s.HotkeyNative.RegisteredHotkeys.Single(h => h.Vk == 0x44);
        s.HotkeyNative.SendWmHotkey(registered.Id, (uint)binding.Modifiers, registered.Vk);
        await s.Orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await s.Hotkeys.WaitForInFlightDispatchesAsync(); await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
        s.Presenter.ViewModel.InputText.ShouldBe(raw);
        s.Orchestrator.Requests.Single().Origin.ShouldBe(TextOrigin.Selection);
        s.Preprocessor.Calls.ShouldContain(c => c.Raw == raw && c.Origin == TextOrigin.Selection && c.Prepared == "Hello world");
        s.WindowsCreated.ShouldBe(1);
    });

    [Fact]
    public Task ActualAppHostBuilder_RegistersUniqueHandlerAndSharedReaderPresenterWithoutIo() => WpfTestRunner.RunAsync(async () =>
    {
        var paths = Substitute.For<IAppPaths>(); paths.DataDirectory.Returns(Path.GetTempPath());
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings());
        var desktop = Substitute.For<IDesktopContext>();
        var native = Substitute.For<IClipboardSnapshotNative>(); var keys = Substitute.For<INativeKeyboardInputAdapter>();
        var host = AppHostBuilder.Build([], paths, new Serilog.Core.LoggingLevelSwitch(), 0, services =>
        {
            services.AddSingleton(settings); services.AddSingleton(desktop); services.AddSingleton(native); services.AddSingleton(keys);
            services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(Substitute.For<IClipboardService>());
            services.AddSingleton(Substitute.For<ITranslationOrchestrator>()); services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher());
        });
        try
        {
            var provider = host.Services;
            provider.GetServices<IActionHandler>().OfType<SelectionTranslateHandler>().Count().ShouldBe(1);
            provider.GetRequiredService<ISelectionTextReader>().ShouldBeSameAs(provider.GetRequiredService<SelectionReadCoordinator>());
            provider.GetRequiredService<ITranslateWindowPresenter>().ShouldBeSameAs(provider.GetRequiredService<TranslateWindowPresenter>());
            native.ReceivedCalls().ShouldBeEmpty(); keys.ReceivedCalls().ShouldBeEmpty(); desktop.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            await host.Services.GetRequiredService<SelectionReadCoordinator>().DisposeAsync();
            await host.Services.GetRequiredService<CompositeSelectionReader>().DisposeAsync();
            await host.Services.GetRequiredService<TranslateWindowPresenter>().ShutdownAsync();
            host.Dispose();
        }
    });
}
