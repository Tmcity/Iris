using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Workflows;

public sealed class TranslateTextHandlerTests
{
    [Fact]
    public void Action_ReturnsTranslateText()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new TranslateTextHandler(presenter);

        handler.Action.ShouldBe(IrisAction.TranslateText);
    }

    [Fact]
    public void Constructor_WithNullPresenter_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new TranslateTextHandler(null!));
    }

    [Fact]
    public async Task ExecuteAsync_WithNullContext_ThrowsArgumentNullException()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new TranslateTextHandler(presenter);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            handler.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationRequested_ThrowsOperationCanceledException_AndDoesNotCallPresenter()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new TranslateTextHandler(presenter);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new ActionContext(ActionTrigger.CommandLine, Payload: "test");

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(context, cts.Token));

        presenter.DidNotReceive().TryBeginOperation(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task ExecuteAsync_WhenPayloadIsNullOrWhitespace_ThrowsInputEmptyIrisException_AndDoesNotCallPresenter(string? payload)
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new TranslateTextHandler(presenter);

        var context = new ActionContext(ActionTrigger.CommandLine, Payload: payload);

        var ex = await Should.ThrowAsync<IrisException>(() =>
            handler.ExecuteAsync(context, CancellationToken.None));

        ex.Code.ShouldBe(ErrorCode.InputEmpty);
        ex.Message.ShouldBe(ErrorMessages.GetDefault(ErrorCode.InputEmpty));

        presenter.DidNotReceive().TryBeginOperation(Arg.Any<CancellationToken>());
        await presenter.DidNotReceive().TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>());
    }

    [Theory]
    [InlineData(ActionTrigger.CommandLine, TextOrigin.CommandLine)]
    [InlineData(ActionTrigger.DragDrop, TextOrigin.FloatingBall)]
    [InlineData(ActionTrigger.FloatingBall, TextOrigin.FloatingBall)]
    [InlineData(ActionTrigger.Hotkey, TextOrigin.Input)]
    [InlineData(ActionTrigger.TrayMenu, TextOrigin.Input)]
    [InlineData(ActionTrigger.SelectionIcon, TextOrigin.Input)]
    [InlineData(ActionTrigger.MouseSelection, TextOrigin.Input)]
    [InlineData(ActionTrigger.InApp, TextOrigin.Input)]
    public async Task ExecuteAsync_WithValidPayload_MapsTriggerToExpectedOrigin_AndPublishes(ActionTrigger trigger, TextOrigin expectedOrigin)
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var handler = new TranslateTextHandler(presenter);
        var anchor = new PixelPoint(123, 456);
        const string rawPayload = "  raw payload text with spaces  ";
        var context = new ActionContext(trigger, Anchor: anchor, Payload: rawPayload);

        await handler.ExecuteAsync(context, CancellationToken.None);

        presenter.Received(1).TryBeginOperation(Arg.Any<CancellationToken>());
        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == rawPayload &&
            r.Origin == expectedOrigin &&
            r.Anchor == anchor &&
            r.AutoTranslate == true &&
            r.FocusInput == false));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryBeginOperationReturnsNull_QuietlyExits_WithoutCallingTryShowAsync()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns((ITranslateWindowOperation?)null);

        var handler = new TranslateTextHandler(presenter);
        var context = new ActionContext(ActionTrigger.CommandLine, Payload: "hello");

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.DidNotReceive().TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryShowReturnsFalse_QuietlyExits_AndDisposesOperation()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(false));

        var handler = new TranslateTextHandler(presenter);
        var context = new ActionContext(ActionTrigger.CommandLine, Payload: "hello");

        await handler.ExecuteAsync(context, CancellationToken.None);

        operation.Received(1).Dispose();
    }
}
