using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Workflows;

public sealed class MouseSelectionWorkflowTests
{
    [Theory]
    [InlineData(ActionTrigger.SelectionIcon, MouseSelectionMode.ShowIcon, " original\r\ntext ", 0, true)]
    [InlineData(ActionTrigger.MouseSelection, MouseSelectionMode.AutoTranslate, null, 1, true)]
    [InlineData(ActionTrigger.SelectionIcon, MouseSelectionMode.Off, "cached", 0, false)]
    [InlineData(ActionTrigger.MouseSelection, MouseSelectionMode.ShowIcon, null, 0, false)]
    public async Task MouseBranches_UseOneLeaseAndSavedMode(ActionTrigger trigger, MouseSelectionMode mode, string? payload, int reads, bool shown)
    {
        var f = new Fixture(mode);
        var context = new ActionContext(trigger, new(7, 8), payload) { ExpectedSelectionSource = f.App with { WindowTitle = "different title" } };
        await f.Handler.ExecuteAsync(context, TestContext.Current.CancellationToken);
        f.Reader.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ReadAsync").ShouldBe(reads);
        f.Presenter.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "TryBeginOperation").ShouldBe(shown ? 1 : 0);
        f.SettingsReads.ShouldBe(1);
        if (shown)
            await f.Presenter.Received(1).TryShowAsync(f.Operation, Arg.Is<TranslateWindowRequest>(r => r.Text == (payload ?? "read once") && r.AutoTranslate && r.Origin == TextOrigin.Selection && !r.FocusInput));
        else f.Presenter.ReceivedCalls().ShouldBeEmpty();
    }
    [Theory]
    [InlineData(ActionTrigger.SelectionIcon, null, true)]
    [InlineData(ActionTrigger.SelectionIcon, " ", true)]
    [InlineData(ActionTrigger.SelectionIcon, "cached", false)]
    [InlineData(ActionTrigger.MouseSelection, "not empty", true)]
    public async Task InvalidMouseArguments_AreSafeBeforeLease(ActionTrigger trigger, string? payload, bool source)
    {
        var f = new Fixture(MouseSelectionMode.ShowIcon);
        var context = new ActionContext(trigger, Payload: payload) { ExpectedSelectionSource = source ? f.App : null };
        (await Should.ThrowAsync<IrisException>(() => f.Handler.ExecuteAsync(context, TestContext.Current.CancellationToken))).Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Presenter.ReceivedCalls().ShouldBeEmpty(); f.Reader.ReceivedCalls().ShouldBeEmpty();
        context.ToString().ShouldBe($"ActionContext {{ Trigger={trigger} }}");
    }
    [Fact]
    public async Task AutoEmpty_IsSilentEvenWhenOpenInputIsEnabled()
    {
        var f = new Fixture(MouseSelectionMode.AutoTranslate);
        f.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<SelectionReadResult?>(null));
        await f.Handler.ExecuteAsync(new(ActionTrigger.MouseSelection) { ExpectedSelectionSource = f.App }, TestContext.Current.CancellationToken);
        f.Presenter.ReceivedCalls().ShouldNotContain(c => (c.GetMethodInfo().Name == "TryShowAsync" || c.GetMethodInfo().Name == "TryNotifyAsync"));
        f.Operation.Received(1).Dispose();
    }
    [Theory]
    [InlineData(ActionTrigger.SelectionIcon)]
    [InlineData(ActionTrigger.MouseSelection)]
    [InlineData(ActionTrigger.Hotkey)]
    public async Task ExpectedSourceMismatch_DoesNotReadOrOpenOwnInput(ActionTrigger trigger)
    {
        var f = new Fixture(trigger == ActionTrigger.SelectionIcon ? MouseSelectionMode.ShowIcon : MouseSelectionMode.AutoTranslate);
        f.Desktop.GetForegroundApp().Returns(f.App with { ProcessId = Environment.ProcessId, WindowHandle = 88 });
        await f.Handler.ExecuteAsync(new(trigger, Payload: trigger == ActionTrigger.SelectionIcon ? "cached" : null) { ExpectedSelectionSource = f.App }, TestContext.Current.CancellationToken);
        f.Reader.ReceivedCalls().ShouldBeEmpty();
        f.Presenter.ReceivedCalls().ShouldNotContain(c => (c.GetMethodInfo().Name == "TryShowAsync" || c.GetMethodInfo().Name == "TryNotifyAsync"));
    }
    [Fact]
    public async Task ExplicitExpectedSource_IsForwardedAndCaptureErrorHasApprovedDefault()
    {
        var f = new Fixture(MouseSelectionMode.Off);
        await f.Handler.ExecuteAsync(new(ActionTrigger.Hotkey) { ExpectedSelectionSource = f.App }, TestContext.Current.CancellationToken);
        await f.Reader.Received(1).ReadAsync(Arg.Is<SelectionReadOptions>(o => o.ExpectedSource != null && o.ExpectedSource.WindowTitle == null && o.ExpectedSource.WindowHandle == f.App.WindowHandle), Arg.Any<CancellationToken>());
        ErrorMessages.GetDefault(ErrorCode.CaptureFailed).ShouldBe("内容获取失败，请稍后重试");
    }
    private sealed class Fixture
    {
        internal readonly ForegroundAppInfo App = new("editor.exe", 5000, "private title", 123);
        internal readonly ISettingsService Settings = Substitute.For<ISettingsService>();
        internal readonly IDesktopContext Desktop = Substitute.For<IDesktopContext>();
        internal readonly ISelectionTextReader Reader = Substitute.For<ISelectionTextReader>();
        internal readonly ITranslateWindowPresenter Presenter = Substitute.For<ITranslateWindowPresenter>();
        internal readonly ITranslateWindowOperation Operation = Substitute.For<ITranslateWindowOperation>();
        internal readonly SelectionTranslateHandler Handler;
        internal int SettingsReads;
        internal Fixture(MouseSelectionMode mode)
        {
            Settings.Current.Returns(_ => { SettingsReads++; return new AppSettings { Selection = new() { MouseMode = mode, OpenInputWhenEmpty = true } }; });
            Desktop.GetForegroundApp().Returns(App);
            Presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(Operation);
            Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(new SelectionReadResult("read once", SelectionReadMethod.UIAutomation, App));
            Handler = new(Presenter, Reader, Settings, Desktop, NullLogger<SelectionTranslateHandler>.Instance);
        }
    }
}
