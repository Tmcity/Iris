using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsWindowPresenterTests
{
    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            return callback();
        }
    }

    private sealed class ScopedDisposableWitness : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    [Theory]
    [InlineData(null, "general")]
    [InlineData("", "general")]
    [InlineData("   ", "general")]
    [InlineData("general", "general")]
    [InlineData("GENERAL", "general")]
    [InlineData("General", "general")]
    [InlineData("translation", "general")]
    [InlineData("ocr", "general")]
    [InlineData("unknown_page", "general")]
    public void NormalizePageKey_AlwaysReturnsGeneral(string? input, string expected)
    {
        SettingsWindowPresenter.NormalizePageKey(input).ShouldBe(expected);
    }

    [Fact]
    public async Task SettingsWindow_XamlProperties_MatchSpecification()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.Title.ShouldBe(UiText.SettingsWindowTitle);
                window.MinWidth.ShouldBe(600);
                window.MinHeight.ShouldBe(440);
                window.Width.ShouldBe(760);
                window.Height.ShouldBe(520);
                window.WindowStartupLocation.ShouldBe(WindowStartupLocation.CenterScreen);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ShowAsync_InStaThread_CreatesSession_BindsViewModel_ReusesActiveWindow_AndCleansUpOnClose()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            presenter.ActiveSession.ShouldBeNull();

            // 首次 Show
            await presenter.ShowAsync("general");

            presenter.ActiveSession.ShouldNotBeNull();
            presenter.ActiveSession.IsOpen.ShouldBeTrue();
            var window = presenter.ActiveSession.Window;
            window.ShouldNotBeNull();
            window.DataContext.ShouldBeSameAs(presenter.ActiveSession.ViewModel);

            // 重复 Show 保留现有会话与窗口
            await presenter.ShowAsync("translation");
            presenter.ActiveSession.ShouldNotBeNull();
            presenter.ActiveSession.Window.ShouldBeSameAs(window);

            // 关闭窗口后会话清空
            window.Close();
            presenter.ActiveSession.ShouldBeNull();

            // 再次 Show 会创建新会话与新窗口
            await presenter.ShowAsync();
            presenter.ActiveSession.ShouldNotBeNull();
            presenter.ActiveSession.Window.ShouldNotBeSameAs(window);

            // 正常关停
            await presenter.ShutdownAsync();
            presenter.ActiveSession.ShouldBeNull();
        });
    }

    [Fact]
    public async Task ShutdownAsync_ClosesActiveWindow_AndPreventsSubsequentShow()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            await presenter.ShowAsync("general");
            presenter.ActiveSession.ShouldNotBeNull();

            await presenter.ShutdownAsync();
            presenter.ActiveSession.ShouldBeNull();

            // 关停后再调用 ShowAsync 应该被直接忽略
            await presenter.ShowAsync("general");
            presenter.ActiveSession.ShouldBeNull();
        });
    }

    [Fact]
    public async Task ShowAsync_WhenWindowFactoryThrows_RollsBackSession_AndDisposesScopedResources()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            ScopedDisposableWitness? witness = null;

            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            services.AddScoped<ScopedDisposableWitness>(sp =>
            {
                witness = new ScopedDisposableWitness();
                return witness;
            });
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                throw new InvalidOperationException("Window factory simulated failure");
            });

            var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            var ex = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
            ex.Message.ShouldContain("Window factory simulated failure");

            presenter.ActiveSession.ShouldBeNull();
            witness.ShouldNotBeNull();
            witness!.IsDisposed.ShouldBeTrue("创建失败时 scope 必须回滚并释放已解析的 scoped 资源");
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhenCalledConcurrently_SharesSameTask()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            await presenter.ShowAsync("general");
            presenter.ActiveSession.ShouldNotBeNull();

            var task1 = presenter.ShutdownAsync();
            var task2 = presenter.ShutdownAsync();

            task1.ShouldBeSameAs(task2);

            await Task.WhenAll(task1, task2);
            presenter.ActiveSession.ShouldBeNull();
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhileSaveFinallyPending_AwaitsRealSaveTask_ClosesWindow_AndDisposesScopeWithoutOde()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    saveEntered.TrySetResult();
                    await saveRelease.Task;
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                });

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            await presenter.ShowAsync("general");
            var session = presenter.ActiveSession;
            session.ShouldNotBeNull();
            var vm = session!.ViewModel;
            var window = session.Window;

            // 发起保存，使任务在 UpdateAsync 内等待门闩
            vm.Theme = AppTheme.Dark;
            var saveTask = vm.ExecuteSaveAsync();

            await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 发起关停：由于保存尚未真正完成，关停任务不能提前返回
            var shutdownTask = presenter.ShutdownAsync();

            // 放行保存底层任务
            saveRelease.TrySetResult();

            // 等待关停完成
            await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 观察底层保存任务结果（无 ObjectDisposedException）
            await saveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // 断言会话与窗口均已安全退出
            presenter.ActiveSession.ShouldBeNull();
            window.IsVisible.ShouldBeFalse();
        });
    }
}
