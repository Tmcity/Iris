using Iris.Core.Actions;
using Iris.Core.Presentation;
using Iris.Core.Workflows;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Workflows;

public sealed class OpenHistoryHandlerTests
{
    [Fact]
    public async Task OpenHistory_UsesExistingPresenterExactlyOnce()
    {
        var presenter = Substitute.For<IHistoryWindowPresenter>(); var handler = new OpenHistoryHandler(presenter);
        handler.Action.ShouldBe(IrisAction.OpenHistory);
        await handler.ExecuteAsync(new(ActionTrigger.TrayMenu), TestContext.Current.CancellationToken);
        await presenter.Received(1).ShowAsync();
    }
    [Fact]
    public async Task CanceledCaller_DoesNotShowHistory()
    {
        var presenter = Substitute.For<IHistoryWindowPresenter>(); var handler = new OpenHistoryHandler(presenter);
        await Should.ThrowAsync<OperationCanceledException>(() => handler.ExecuteAsync(new(ActionTrigger.TrayMenu), new CancellationToken(true)));
        await presenter.DidNotReceive().ShowAsync();
    }
}
