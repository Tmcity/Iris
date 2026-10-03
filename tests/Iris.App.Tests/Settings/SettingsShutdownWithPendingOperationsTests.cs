using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Common;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

[Collection("AppShutdownerSharedState")]
public sealed class SettingsShutdownWithPendingOperationsTests
{
    private sealed class ScopedDisposableWitness : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task ShutdownAsync_WhileDraftTestInFlight_AwaitsRealWork_AndDisposesCleanly()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var testStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var testReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
            editCoordinator.TestDraftAsync(Arg.Any<TranslationProviderDraft>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    testStarted.TrySetResult();
                    await testReleaseTcs.Task;
                    return new ProviderTestResult(true, TimeSpan.FromMilliseconds(50), "sample");
                });

            var receiptManager = new SettingsApplyReceiptManager();
            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);
            services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            ScopedDisposableWitness? witness = null;
            services.AddScoped<ScopedDisposableWitness>(sp =>
            {
                witness = new ScopedDisposableWitness();
                return witness;
            });

            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                return new GeneralSettingsSaveCoordinator(
                    settingsService,
                    editCoordinator,
                    autoStartService,
                    receiptManager,
                    NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });

            services.AddScoped<SettingsViewModel>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                return new SettingsViewModel(
                    settingsService,
                    sp.GetRequiredService<ThemeService>(),
                    sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
                    editCoordinator,
                    receiptManager,
                    uiDispatcher,
                    NullLogger<SettingsViewModel>.Instance);
            });

            services.AddTransient<SettingsWindow>();

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("translation");
                var session = presenter.ActiveSession;
                session.ShouldNotBeNull();
                var vm = session!.ViewModel;
                var window = session.Window;
                witness.ShouldNotBeNull();

                // 添加一个 Profile 并发起测试
                vm.AddProfileFromPreset(SettingsViewModel.AvailablePresets[0]);
                vm.SelectedProfile!.Model = "gpt-4o";
                var testTask = vm.ExecuteTestDraftAsync();

                await testStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

                // 发起关停：由于草稿测试在途，关停任务处于挂起状态，且 scope 与窗口存活
                var shutdownTask = presenter.ShutdownAsync();

                shutdownTask.IsCompleted.ShouldBeFalse("关停在测试进行中不得提前返回完成");
                witness!.IsDisposed.ShouldBeFalse("关停等待期间 scope 必须存活");
                window.IsVisible.ShouldBeTrue("关停等待期间窗口必须保持可见与存活");

                // 放行底层测试任务
                testReleaseTcs.TrySetResult();

                await Task.WhenAll(shutdownTask, testTask).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

                presenter.ActiveSession.ShouldBeNull();
                window.IsVisible.ShouldBeFalse();
                witness.IsDisposed.ShouldBeTrue();
            }
            finally
            {
                testReleaseTcs.TrySetResult();
                await presenter.ShutdownAsync();
            }
        });
    }
}
