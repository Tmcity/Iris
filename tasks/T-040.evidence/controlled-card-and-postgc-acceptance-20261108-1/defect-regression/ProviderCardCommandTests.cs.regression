using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Iris.App.Tests.TestSupport;
using Iris.App.Translation;
using Shouldly;

namespace Iris.App.Tests.Translation;

public sealed class ProviderCardCommandTests
{
    [Theory]
    [InlineData("copy-stream-and-reset")]
    [InlineData("retry-terminal-and-reset")]
    [InlineData("retry-operation-ownership")]
    public Task CardCommands_UpdateRealButtonsWithoutInputRequery(string transition) => WpfTestRunner.RunAsync(() =>
    {
        bool ownsOperation = true;
        var card = new ProviderResultCardViewModel(Guid.NewGuid(), "owned-provider", "Owned provider", "model",
            Guid.NewGuid(), _ => { }, _ => { }, _ => { }, _ => ownsOperation);
        var button = new Button
        {
            Command = transition == "copy-stream-and-reset" ? card.CopyCommand : card.RetryCommand,
        };
        try
        {
            if (transition == "copy-stream-and-reset")
            {
                button.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(card.CanCopy)) { Source = card });
                button.IsEnabled.ShouldBeFalse();
                card.AppendText("owned streaming result");
                card.CanCopy.ShouldBeTrue();
                button.IsEnabled.ShouldBeTrue();
                card.ResetForRetry(Guid.NewGuid());
                button.IsEnabled.ShouldBeFalse();
            }
            else if (transition == "retry-terminal-and-reset")
            {
                button.IsEnabled.ShouldBeFalse();
                card.State = CardState.Streaming;
                card.SetCompleted("owned completed result", TimeSpan.FromMilliseconds(20), false, false, false);
                card.CanRetry.ShouldBeTrue();
                button.IsEnabled.ShouldBeTrue();
                card.ResetForRetry(Guid.NewGuid());
                button.IsEnabled.ShouldBeFalse();
                card.SetFailed("owned safe error", false, TimeSpan.FromMilliseconds(20));
                button.IsEnabled.ShouldBeTrue();
            }
            else
            {
                card.State = CardState.Completed;
                button.Command = null;
                button.Command = card.RetryCommand;
                button.IsEnabled.ShouldBeTrue();
                ownsOperation = false;
                card.RefreshCanRetry();
                card.CanRetry.ShouldBeFalse();
                button.IsEnabled.ShouldBeFalse();
                ownsOperation = true;
                card.RefreshCanRetry();
                button.IsEnabled.ShouldBeTrue();
            }
        }
        finally
        {
            BindingOperations.ClearBinding(button, UIElement.IsEnabledProperty);
            button.Command = null;
        }
    });
}
