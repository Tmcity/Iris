using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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
                window.ForceClose = true;
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

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
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
            }
            finally
            {
                // 正常关停
                await presenter.ShutdownAsync();
                presenter.ActiveSession.ShouldBeNull();
            }
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

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                presenter.ActiveSession.ShouldNotBeNull();

                await presenter.ShutdownAsync();
                presenter.ActiveSession.ShouldBeNull();

                // 关停后再调用 ShowAsync 应该被直接忽略
                await presenter.ShowAsync("general");
                presenter.ActiveSession.ShouldBeNull();
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
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

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                var ex = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
                ex.Message.ShouldContain("Window factory simulated failure");

                presenter.ActiveSession.ShouldBeNull();
                witness.ShouldNotBeNull();
                witness!.IsDisposed.ShouldBeTrue("创建失败时 scope 必须回滚并释放已解析的 scoped 资源");
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShowAsync_WhenSourceInitializedThrows_CleansUpHwndAndWindow()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var services = new ServiceCollection();
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            ScopedDisposableWitness? witness = null;
            SettingsWindow? windowRef = null;

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

            EventHandler failHandler = (_, _) => throw new InvalidOperationException("Injected source initialization failure");
            services.AddTransient<SettingsWindow>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                var win = new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
                win.SourceInitialized += failHandler;
                windowRef = win;
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                var ex = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
                ex.Message.ShouldContain("Injected source initialization failure");

                presenter.ActiveSession.ShouldBeNull();
                witness.ShouldNotBeNull();
                witness!.IsDisposed.ShouldBeTrue("Show 失败时 scope 必须释放");

                windowRef.ShouldNotBeNull();
                var retained = Application.Current.Windows.Cast<Window>().Contains(windowRef!);
                retained.ShouldBeFalse("失败的窗口不应残留在 Application.Windows");

                var hasHandle = new WindowInteropHelper(windowRef!).Handle != IntPtr.Zero;
                hasHandle.ShouldBeFalse("回滚后原生 HWND 必须已被销毁");
            }
            finally
            {
                if (windowRef != null)
                {
                    windowRef.SourceInitialized -= failHandler;
                    windowRef.ForceClose = true;
                    windowRef.Close();
                }
                await presenter.ShutdownAsync();
            }
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

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                presenter.ActiveSession.ShouldNotBeNull();

                var task1 = presenter.ShutdownAsync();
                var task2 = presenter.ShutdownAsync();

                task1.ShouldBeSameAs(task2);

                await Task.WhenAll(task1, task2);
                presenter.ActiveSession.ShouldBeNull();
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhileSaveFinallyPending_AwaitsRealSaveTask_ClosesWindow_AndDisposesScopeWithoutOde()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var saveRelease = new ManualResetEventSlim();

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    await Task.CompletedTask;
                });

            var autoStartService = Substitute.For<IAutoStartService>();
            autoStartService.When(x => x.SetEnabled(Arg.Any<bool>())).Do(_ =>
            {
                saveStarted.TrySetResult();
                if (!saveRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Save release wait timed out");
                }
            });

            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
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
                return new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                var session = presenter.ActiveSession;
                session.ShouldNotBeNull();
                var vm = session!.ViewModel;
                var window = session.Window;
                witness.ShouldNotBeNull();

                // 发起保存，使任务在 SetEnabled 内等待门闩（此时 JSON 已提交，正在进行后台系统应用）
                vm.LaunchAtStartup = true;
                var saveTask = vm.ExecuteSaveAsync();

                await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 发起关停：由于保存尚未真正完成，关停任务必须处于挂起状态，且 scope 与窗口必须存活
                var shutdownTask = presenter.ShutdownAsync();

                shutdownTask.IsCompleted.ShouldBeFalse("关停在保存/收尾进行中不得提前返回完成");
                witness!.IsDisposed.ShouldBeFalse("关停等待期间 scope 必须存活");
                window.IsVisible.ShouldBeTrue("关停等待期间窗口必须保持可见与存活");

                // 放行保存底层任务
                saveRelease.Set();

                // 等待保存与关停完成
                await Task.WhenAll(shutdownTask, saveTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 断言会话与窗口均已安全退出，scope 释放，且无 ODE
                presenter.ActiveSession.ShouldBeNull();
                window.IsVisible.ShouldBeFalse();
                witness.IsDisposed.ShouldBeTrue();
            }
            finally
            {
                saveRelease.Set();
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhileRetryAutoStartPending_AwaitsRealRetryTask_AndCleansUp()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var retryRelease = new ManualResetEventSlim();

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { LaunchAtStartup = true } });

            var autoStartService = Substitute.For<IAutoStartService>();
            autoStartService.When(x => x.SetEnabled(true)).Do(_ =>
            {
                retryStarted.TrySetResult();
                if (!retryRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Retry release wait timed out");
                }
            });

            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
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
                return new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>();

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                var session = presenter.ActiveSession;
                session.ShouldNotBeNull();
                var vm = session!.ViewModel;
                var window = session.Window;

                var retryTask = vm.ExecuteRetryAutoStartAsync();

                await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                var shutdownTask = presenter.ShutdownAsync();

                shutdownTask.IsCompleted.ShouldBeFalse("关停在重试收尾进行中不得提前返回完成");
                witness!.IsDisposed.ShouldBeFalse("关停等待期间 scope 必须存活");

                retryRelease.Set();

                await Task.WhenAll(shutdownTask, retryTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                presenter.ActiveSession.ShouldBeNull();
                window.IsVisible.ShouldBeFalse();
                witness.IsDisposed.ShouldBeTrue();
            }
            finally
            {
                retryRelease.Set();
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task CloseDuringQuery_NormalClose_ScopeRetainedUntilQueryCompletes_AndShutdownWaits()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var queryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var queryRelease = new ManualResetEventSlim();

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            autoStartService.IsEnabled().Returns(_ =>
            {
                queryEntered.TrySetResult();
                if (!queryRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Query release wait timed out");
                }
                return false;
            });

            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            ScopedDisposableWitness? witness = null;
            GeneralSettingsSaveCoordinator? coordinator = null;
            services.AddScoped<ScopedDisposableWitness>(sp =>
            {
                witness = new ScopedDisposableWitness();
                return witness;
            });
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                coordinator = new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
                return coordinator;
            });
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>(sp =>
            {
                return new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                var session = presenter.ActiveSession;
                session.ShouldNotBeNull();
                var window = session!.Window;

                // 等待初始查询进入 AutoStartService.IsEnabled
                await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                var activeQuery = coordinator!.ActiveWorkTask;
                (activeQuery != null).ShouldBeTrue();
                activeQuery!.IsCompleted.ShouldBeFalse();

                // 执行正常窗口关闭
                window.Close();

                // 在查询完成前，scope 必须存活，且 Presenter 仍持有 active session
                witness!.IsDisposed.ShouldBeFalse("普通关闭在查询完成前不得释放 scope");
                presenter.ActiveSession.ShouldNotBeNull("普通关闭在清理完成前 Presenter 必须保留 session 引用");

                var shutdownTask = presenter.ShutdownAsync();
                shutdownTask.IsCompleted.ShouldBeFalse("查询未完成时 ShutdownAsync 必须等待排干");

                // 放行查询
                queryRelease.Set();

                await Task.WhenAll(shutdownTask, activeQuery).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                presenter.ActiveSession.ShouldBeNull();
                window.IsVisible.ShouldBeFalse();
                witness.IsDisposed.ShouldBeTrue("所有后台工作排干后 scope 最终释放");
            }
            finally
            {
                queryRelease.Set();
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task CloseDuringQuery_ShowFailure_ScopeRetainedUntilQueryCompletes_AndShutdownWaits()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var queryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var queryRelease = new ManualResetEventSlim();

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            autoStartService.IsEnabled().Returns(_ =>
            {
                queryEntered.TrySetResult();
                if (!queryRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Query release wait timed out");
                }
                return false;
            });

            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            ScopedDisposableWitness? witness = null;
            GeneralSettingsSaveCoordinator? coordinator = null;
            services.AddScoped<ScopedDisposableWitness>(sp =>
            {
                witness = new ScopedDisposableWitness();
                return witness;
            });
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                coordinator = new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
                return coordinator;
            });
            services.AddScoped<SettingsViewModel>();

            EventHandler sourceInitializedFailure = (_, _) =>
            {
                if (!queryEntered.Task.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Query did not enter before source initialization");
                }
                throw new InvalidOperationException("Injected source initialization failure");
            };

            services.AddTransient<SettingsWindow>(sp =>
            {
                var win = new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
                win.SourceInitialized += sourceInitializedFailure;
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                var showEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
                showEx.Message.ShouldContain("Injected source initialization failure");

                var activeQuery = coordinator!.ActiveWorkTask;
                (activeQuery != null).ShouldBeTrue();
                activeQuery!.IsCompleted.ShouldBeFalse();

                // Show 失败但异步查询仍挂起时：scope 仍存活，presenter 仍然持有 session 以便追踪清理
                witness!.IsDisposed.ShouldBeFalse("Show 失败但在查询完成前 scope 必须存活");
                presenter.ActiveSession.ShouldNotBeNull("Show 失败但清理未完成前 Presenter 必须保留 session 引用");

                var shutdownTask = presenter.ShutdownAsync();
                shutdownTask.IsCompleted.ShouldBeFalse("Show 失败路径的 ShutdownAsync 必须等待挂起的查询排干");

                // 放行查询
                queryRelease.Set();

                await Task.WhenAll(shutdownTask, activeQuery).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                presenter.ActiveSession.ShouldBeNull();
                witness.IsDisposed.ShouldBeTrue("所有后台工作排干后 scope 最终释放");
            }
            finally
            {
                queryRelease.Set();
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhenClosingCancelled_PreservesScopeAndWindow_AndObservesError()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new ImmediateUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
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
                return new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>(sp =>
            {
                return new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                var session = presenter.ActiveSession;
                session.ShouldNotBeNull();
                var window = session!.Window;

                System.ComponentModel.CancelEventHandler cancelHandler = (_, args) => args.Cancel = true;
                window.Closing += cancelHandler;

                try
                {
                    // 第一次 Shutdown：因 Closing 被取消，内部安全记录错误，不抛出崩损
                    await presenter.ShutdownAsync();

                    // 窗口必须保持可见与存活，scope 不得释放
                    window.IsVisible.ShouldBeTrue("取消关闭后窗口必须保持可见");
                    new WindowInteropHelper(window).Handle.ShouldNotBe(IntPtr.Zero);
                    witness!.IsDisposed.ShouldBeFalse("取消关闭后 scope 必须保持存活");
                    presenter.ActiveSession.ShouldNotBeNull();
                }
                finally
                {
                    window.Closing -= cancelHandler;
                }

                // 取消 Handler 移除后，再次 ShutdownAsync 应该顺利成功
                await presenter.ShutdownAsync();

                presenter.ActiveSession.ShouldBeNull();
                window.IsVisible.ShouldBeFalse();
                witness!.IsDisposed.ShouldBeTrue("关闭完成后 scope 最终释放");
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }
}

