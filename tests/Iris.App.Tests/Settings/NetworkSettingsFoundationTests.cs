using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.DependencyInjection;
using Iris.Core.Network;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Settings;

[Collection("ScreenshotRendering")]
public sealed class NetworkSettingsFoundationTests
{
    [Fact]
    public async Task NetworkPlansShareOriginalReceiptQuotaAndRetryNeverRecommits()
    {
        var settings = Substitute.For<ISettingsService>(); settings.Current.Returns(new AppSettings());
        var coordinator = Substitute.For<ISettingsEditCoordinator>(); var receipts = new SettingsApplyReceiptManager();
        var receipt = Substitute.For<ISettingsApplyReceipt>(); receipt.CommitId.Returns(Guid.NewGuid());
        coordinator.CommitAsync(Arg.Any<SettingsEditPlan>(), Arg.Any<CancellationToken>()).Returns(new SettingsEditResult(SettingsEditStatus.ApplyPending, PendingApply: receipt));
        coordinator.RetryApplyAsync(receipt, Arg.Any<CancellationToken>()).Returns(new SettingsApplyResult(true));
        using var save = new GeneralSettingsSaveCoordinator(settings, coordinator, Substitute.For<IAutoStartService>(), receipts, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
        var network = new SettingsEditPlan(new(), []) { Network = new(ProxyMode: new(ProxyMode.System, ProxyMode.None)) };
        (await save.CommitPlanAsync(network, false, false, TestContext.Current.CancellationToken)).Status.ShouldBe(GeneralSaveStatus.SavedApplicationPending);
        receipts.PendingCount.ShouldBe(1); receipts.InFlightReservations.ShouldBe(0);
        for (int i = 1; i < receipts.MaxCapacity; i++) receipts.TryReserveSlot().ShouldBeTrue();
        (await save.CommitPlanAsync(network, false, false, TestContext.Current.CancellationToken)).IsQuotaFull.ShouldBeTrue();
        await coordinator.Received(1).CommitAsync(Arg.Any<SettingsEditPlan>(), Arg.Any<CancellationToken>());
        // The original opaque receipt is the only retry input; the plan is never passed again.
        (await coordinator.RetryApplyAsync(receipt, TestContext.Current.CancellationToken)).IsComplete.ShouldBeTrue();
        receipts.RemoveReceipt(receipt).ShouldBeTrue();
        await coordinator.Received(1).CommitAsync(Arg.Any<SettingsEditPlan>(), Arg.Any<CancellationToken>());
        while (receipts.InFlightReservations != 0) receipts.ReleaseReservation();
    }

    [Fact]
    public async Task ActualFutureSettingsWindow_DisablesEditsSaveAndTests_AllowsCloseAndRendersNotice()
    {
        string output = Environment.GetEnvironmentVariable("IRIS_T036B_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "iris-t036b-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        foreach (var width in new[] { 600, 760 })
        {
            await WpfTestRunner.RunAsync(async () =>
            {
                using var dir = new TempDirectory(); var paths = new FakeAppPaths(dir.Path);
                const string original = "{\"schemaVersion\":2147483648,\"network\":{\"customProxyUrl\":\"http://synthetic:private@proxy.example.test/\"}}";
                await File.WriteAllTextAsync(paths.SettingsFile, original, TestContext.Current.CancellationToken);
                var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IAppPaths>(paths); services.AddIrisInfrastructure(); services.AddIrisCore();
                services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(Substitute.For<IAutoStartService>());
                services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>(); services.AddSingleton<ThemeService>();
                services.AddSingleton<ISettingsApplyReceiptManager, SettingsApplyReceiptManager>();
                services.AddScoped<GeneralSettingsSaveCoordinator>(); services.AddScoped<SettingsViewModel>();
                await using var provider = services.BuildServiceProvider(); var settings = provider.GetRequiredService<ISettingsService>();
                await settings.LoadAsync(TestContext.Current.CancellationToken); settings.IsReadOnly.ShouldBeTrue();
                var scope = provider.CreateScope(); var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
                var window = new SettingsWindow { Width = width, Height = width == 600 ? 440 : 520, ShowActivated = false, ShowInTaskbar = false,
                    Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, DataContext = vm };
                var session = new SettingsWindowSession(scope, window, vm, provider.GetRequiredService<ThemeService>(), scope.ServiceProvider.GetRequiredService<GeneralSettingsSaveCoordinator>());
                try
                {
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme == AppTheme.Dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
                    window.Show(); window.UpdateLayout();
                    vm.IsReadOnly.ShouldBeTrue(); vm.CanEdit.ShouldBeFalse(); vm.SaveCommand.CanExecute(null).ShouldBeFalse();
                    vm.TestDraftCommand.CanExecute(null).ShouldBeFalse(); vm.TestOcrCommand.CanExecute(null).ShouldBeFalse();
                    vm.CancelCommand.CanExecute(null).ShouldBeTrue();
                    var before = vm.Theme; vm.Theme = before == AppTheme.Light ? AppTheme.Dark : AppTheme.Light; vm.Theme.ShouldBe(before);
                    await vm.ExecuteSaveAsync(); await vm.ExecuteOcrTestAsync();
                    var notices = Descendants<TextBlock>(window).Where(t => t.Text == UiText.SettingsFutureReadOnly).ToArray(); notices.Length.ShouldBe(1);
                    notices[0].IsVisible.ShouldBeTrue();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var frame = new RenderTargetBitmap(width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); frame.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                    using (var file = File.Create(Path.Combine(output, $"readonly-{theme}-{width}.png"))) encoder.Save(file);
                    var edge = notices[0].TransformToAncestor(window).Transform(new Point(notices[0].ActualWidth, notices[0].ActualHeight));
                    edge.X.ShouldBeLessThanOrEqualTo(window.ActualWidth); edge.Y.ShouldBeLessThanOrEqualTo(window.ActualHeight);
                    provider.GetService<IProxyConnectivityTester>().ShouldBeNull();
                    (await File.ReadAllTextAsync(paths.SettingsFile, TestContext.Current.CancellationToken)).ShouldBe(original);
                }
                finally { await session.ShutdownAsync(); }
                window.IsVisible.ShouldBeFalse();
            });
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
