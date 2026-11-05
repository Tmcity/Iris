using System.Threading;
using System.Threading.Tasks;
using Iris.App.Tests.Services;
using Xunit;

namespace Iris.App.Tests.TestSupport;

// Temporary diagnostic: retain the original callback test and hold the shared
// dispatcher exactly as another test's synchronous fake can hold it.
public sealed class ThemeDispatchInterferenceProbe
{
    [Fact]
    public async Task HeldSharedDispatcher_ReproducesOriginalThemeCallbackTimeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blocker = WpfTestRunner.RunAsync(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            await entered.Task.WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await new ThemeServicePreviewTests().VisualThemeApplier_IsInvoked_WhenThemeChanges();
        }
        finally
        {
            release.Set();
            await blocker;
            await WpfTestRunner.RunAsync(() => Task.CompletedTask);
        }
    }
}
