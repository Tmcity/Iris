using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Imaging;
using Iris.Core.Presentation;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Workflows;

public sealed class InputTranslateHandlerTests
{
    [Fact]
    public void Action_ReturnsInputTranslate()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new InputTranslateHandler(presenter);

        handler.Action.ShouldBe(IrisAction.InputTranslate);
    }

    [Fact]
    public void Constructor_WithNullPresenter_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new InputTranslateHandler(null!));
    }

    [Fact]
    public async Task ExecuteAsync_WithNullContext_ThrowsArgumentNullException()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new InputTranslateHandler(presenter);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            handler.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationRequested_ThrowsOperationCanceledException_AndDoesNotCallPresenter()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var handler = new InputTranslateHandler(presenter);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new ActionContext(ActionTrigger.TrayMenu);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(context, cts.Token));

        await presenter.DidNotReceive().HideAsync();
        presenter.DidNotReceive().TryBeginOperation(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenVisibleAndActive_CallsHideAsync_AndDoesNotBeginOperation()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.IsVisible.Returns(true);
        presenter.IsActive.Returns(true);

        var handler = new InputTranslateHandler(presenter);
        var context = new ActionContext(ActionTrigger.Hotkey);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).HideAsync();
        presenter.DidNotReceive().TryBeginOperation(Arg.Any<CancellationToken>());
        await presenter.DidNotReceive().TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteAsync_WhenNotBothVisibleAndActive_BeginsOperationAndShows(bool isVisible, bool isActive)
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.IsVisible.Returns(isVisible);
        presenter.IsActive.Returns(isActive);

        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var handler = new InputTranslateHandler(presenter);
        var anchor = new PixelPoint(100, 200);
        var context = new ActionContext(ActionTrigger.TrayMenu, Anchor: anchor, Payload: "test");

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.DidNotReceive().HideAsync();
        presenter.Received(1).TryBeginOperation(Arg.Any<CancellationToken>());
        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == "test" &&
            r.Origin == TextOrigin.Input &&
            r.Anchor == anchor &&
            r.FocusInput == true &&
            r.AutoTranslate == true));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPayloadIsNull_SetsAutoTranslateFalse_AndPreservesTextNull()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.IsVisible.Returns(false);
        presenter.IsActive.Returns(false);

        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var handler = new InputTranslateHandler(presenter);
        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: null);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == null &&
            r.Origin == TextOrigin.Input &&
            r.FocusInput == true &&
            r.AutoTranslate == false));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPayloadIsEmptyString_SetsAutoTranslateTrue_AndPreservesEmptyString()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.IsVisible.Returns(false);
        presenter.IsActive.Returns(false);

        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var handler = new InputTranslateHandler(presenter);
        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: "");

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == "" &&
            r.Origin == TextOrigin.Input &&
            r.FocusInput == true &&
            r.AutoTranslate == true));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryBeginOperationReturnsNull_QuietlyExits_WithoutCallingTryShowAsync()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.IsVisible.Returns(false);
        presenter.IsActive.Returns(false);
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns((ITranslateWindowOperation?)null);

        var handler = new InputTranslateHandler(presenter);
        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: "test");

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.DidNotReceive().TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryShowReturnsFalse_QuietlyExits_AndDisposesOperation()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.IsVisible.Returns(false);
        presenter.IsActive.Returns(false);

        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(false));

        var handler = new InputTranslateHandler(presenter);
        var context = new ActionContext(ActionTrigger.TrayMenu, Payload: "test");

        await handler.ExecuteAsync(context, CancellationToken.None);

        operation.Received(1).Dispose();
    }
}
