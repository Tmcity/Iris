using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.Core.Tests.Workflows;

public sealed class ClipboardTranslateHandlerTests
{
    [Fact]
    public void Action_ReturnsClipboardTranslate()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var clipboard = Substitute.For<IClipboardService>();
        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);

        handler.Action.ShouldBe(IrisAction.ClipboardTranslate);
    }

    [Fact]
    public void Constructor_WithNullArguments_ThrowsArgumentNullException()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var clipboard = Substitute.For<IClipboardService>();
        var logger = NullLogger<ClipboardTranslateHandler>.Instance;

        Should.Throw<ArgumentNullException>(() => new ClipboardTranslateHandler(null!, clipboard, logger));
        Should.Throw<ArgumentNullException>(() => new ClipboardTranslateHandler(presenter, null!, logger));
        Should.Throw<ArgumentNullException>(() => new ClipboardTranslateHandler(presenter, clipboard, null!));
    }

    [Fact]
    public async Task ExecuteAsync_WithNullContext_ThrowsArgumentNullException()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var clipboard = Substitute.For<IClipboardService>();
        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            handler.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationRequested_ThrowsOperationCanceledException_AndDoesNotCallPresenterOrClipboard()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var clipboard = Substitute.For<IClipboardService>();
        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new ActionContext(ActionTrigger.TrayMenu);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(context, cts.Token));

        presenter.DidNotReceive().TryBeginOperation(Arg.Any<CancellationToken>());
        await clipboard.DidNotReceive().GetTextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryBeginOperationReturnsNull_DoesNotReadClipboard_AndQuietlyExits()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns((ITranslateWindowOperation?)null);
        var clipboard = Substitute.For<IClipboardService>();
        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);

        var context = new ActionContext(ActionTrigger.TrayMenu);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await clipboard.DidNotReceive().GetTextAsync(Arg.Any<CancellationToken>());
        await presenter.DidNotReceive().TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>());
    }

    [Fact]
    public async Task ExecuteAsync_PassesOperationCancellationTokenToClipboard()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        using var opCts = new CancellationTokenSource();
        operation.CancellationToken.Returns(opCts.Token);
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(opCts.Token).Returns(Task.FromResult<string?>("sample"));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var context = new ActionContext(ActionTrigger.TrayMenu);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await clipboard.Received(1).GetTextAsync(opCts.Token);
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSuccessfulNonEmptyText_PublishesRawTextWithAutoTranslate()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var clipboard = Substitute.For<IClipboardService>();
        var rawText = "  hello world  \r\n";
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(rawText));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var anchor = new PixelPoint(50, 75);
        var context = new ActionContext(ActionTrigger.TrayMenu, Anchor: anchor);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == rawText &&
            r.Origin == TextOrigin.Clipboard &&
            r.Anchor == anchor &&
            r.AutoTranslate == true &&
            r.FocusInput == false &&
            r.Error == null));
        operation.Received(1).Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    public async Task ExecuteAsync_WhenTextIsNullOrEmptyOrWhitespace_PublishesEmptyTextWithInputEmptyError(string? clipboardText)
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(clipboardText));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var anchor = new PixelPoint(50, 75);
        var context = new ActionContext(ActionTrigger.TrayMenu, Anchor: anchor);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == string.Empty &&
            r.Origin == TextOrigin.Clipboard &&
            r.Anchor == anchor &&
            r.AutoTranslate == false &&
            r.FocusInput == true &&
            r.Error != null &&
            r.Error.Code == ErrorCode.InputEmpty &&
            r.Error.Message == ErrorMessages.GetDefault(ErrorCode.InputEmpty)));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClipboardThrowsIrisException_PublishesSafeErrorInfo_WithTextNull()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns<Task<string?>>(_ =>
            throw new IrisException(ErrorCode.ClipboardBusy, "internal sensitive text that must not leak"));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var anchor = new PixelPoint(50, 75);
        var context = new ActionContext(ActionTrigger.TrayMenu, Anchor: anchor);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == null &&
            r.Origin == TextOrigin.Clipboard &&
            r.Anchor == anchor &&
            r.AutoTranslate == false &&
            r.FocusInput == false &&
            r.Error != null &&
            r.Error.Code == ErrorCode.ClipboardBusy &&
            r.Error.Message == ErrorMessages.GetDefault(ErrorCode.ClipboardBusy) &&
            r.Error.Detail == null));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClipboardThrowsUnknownException_PublishesSafeUnknownErrorInfo_WithTextNull()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns<Task<string?>>(_ =>
            throw new InvalidOperationException("native win32 failure details"));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var context = new ActionContext(ActionTrigger.TrayMenu);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Is<TranslateWindowRequest>(r =>
            r.Text == null &&
            r.Origin == TextOrigin.Clipboard &&
            r.AutoTranslate == false &&
            r.FocusInput == false &&
            r.Error != null &&
            r.Error.Code == ErrorCode.Unknown &&
            r.Error.Message == ErrorMessages.GetDefault(ErrorCode.Unknown) &&
            r.Error.Detail == null));
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClipboardThrowsOperationCanceledException_SilentlyExits_WithoutPublishing()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns<Task<string?>>(_ =>
            throw new OperationCanceledException());

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var context = new ActionContext(ActionTrigger.TrayMenu);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.DidNotReceive().TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>());
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryShowReturnsFalse_QuietlyExits_WithoutRetryingOrThrowing()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(false));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("text"));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var context = new ActionContext(ActionTrigger.TrayMenu);

        await handler.ExecuteAsync(context, CancellationToken.None);

        await presenter.Received(1).TryShowAsync(operation, Arg.Any<TranslateWindowRequest>());
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTryShowThrows_PropagatesToCaller_AndDoesNotCatchOrRePublish()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns<Task<bool>>(_ =>
            throw new InvalidOperationException("Presenter failure"));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("text"));

        var handler = new ClipboardTranslateHandler(presenter, clipboard, NullLogger<ClipboardTranslateHandler>.Instance);
        var context = new ActionContext(ActionTrigger.TrayMenu);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            handler.ExecuteAsync(context, CancellationToken.None));

        await presenter.Received(1).TryShowAsync(operation, Arg.Any<TranslateWindowRequest>());
        operation.Received(1).Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClipboardThrows_LogsSafeWarning_WithoutSensitiveDataOrExceptionObject()
    {
        var presenter = Substitute.For<ITranslateWindowPresenter>();
        var operation = Substitute.For<ITranslateWindowOperation>();
        presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(operation);
        presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(Task.FromResult(true));

        var clipboard = Substitute.For<IClipboardService>();
        clipboard.GetTextAsync(Arg.Any<CancellationToken>()).Returns<Task<string?>>(_ =>
            throw new IrisException(ErrorCode.ClipboardBusy, "secret sensitive clipboard text"));

        var logger = Substitute.For<ILogger<ClipboardTranslateHandler>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var handler = new ClipboardTranslateHandler(presenter, clipboard, logger);
        var context = new ActionContext(ActionTrigger.TrayMenu);

        await handler.ExecuteAsync(context, CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            new EventId(1),
            Arg.Is<object>(v => v.ToString()!.Contains("ErrorCode=ClipboardBusy") && v.ToString()!.Contains("ExceptionType=IrisException") && !v.ToString()!.Contains("secret sensitive clipboard text")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
