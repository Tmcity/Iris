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
    [InlineData("translation", "translation")]
    [InlineData("TRANSLATION", "translation")]
    [InlineData("Translation", "translation")]
    [InlineData("ocr", "ocr")]
    [InlineData("SCREENSHOT", "screenshot")]
    [InlineData(" HISTORY ", "history")]
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
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
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
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
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
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
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
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
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
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
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
                coordinator = new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
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
    public async Task CloseDuringQuery_ShowFailure_ScopeRetainedUntilQueryCompletes_AndShowTaskWaitsForCleanup()
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
                coordinator = new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
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

            Task? showTask = null;
            try
            {
                showTask = presenter.ShowAsync("general");

                // 等待进入查询
                await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                var activeQuery = coordinator!.ActiveWorkTask;
                (activeQuery != null).ShouldBeTrue();
                activeQuery!.IsCompleted.ShouldBeFalse();

                // 在放行查询前，ShowAsync 的 Task 绝不能提前完成（必须等待本次创建的所有异步清理与排干）
                showTask.IsCompleted.ShouldBeFalse("清理未完成前 ShowAsync 返回的 Task 绝不能提前结束");
                witness!.IsDisposed.ShouldBeFalse("Show 失败但在查询排干前 scope 必须存活");

                // 放行查询
                queryRelease.Set();

                // 放行后，ShowAsync 最终抛出注入的失败
                var showEx = await Should.ThrowAsync<InvalidOperationException>(() => showTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                showEx.Message.ShouldContain("Injected source initialization failure");

                activeQuery.IsCompleted.ShouldBeTrue("放行后初始查询应当已完成");
                presenter.ActiveSession.ShouldBeNull("Show 失败排干后 Presenter 不应保留死会话");
                witness.IsDisposed.ShouldBeTrue("清理完整结束后 scope 必须已被释放");
            }
            finally
            {
                queryRelease.Set();
                if (showTask != null)
                {
                    try { await showTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); } catch { }
                }
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhenClosingCancelled_PreservesScopeAndWindow_AndPropagatesError()
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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
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
                    // 第一次 Shutdown：因 Closing 被取消，调用方观察到失败
                    var shutdownEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShutdownAsync());
                    shutdownEx.Message.ShouldContain("设置窗口关闭被取消或失败");

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

    [Fact]
    public async Task ShutdownAsync_WhenHiddenWindowClosingCancelled_PreservesScopeAndWindow_AndAllowsRetry()
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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
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

                // 隐藏窗口（持有 HWND 但不可见）
                window.Hide();
                window.IsVisible.ShouldBeFalse();
                new WindowInteropHelper(window).Handle.ShouldNotBe(IntPtr.Zero);

                System.ComponentModel.CancelEventHandler cancelHandler = (_, args) => args.Cancel = true;
                window.Closing += cancelHandler;

                try
                {
                    var shutdownEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShutdownAsync());
                    shutdownEx.Message.ShouldContain("设置窗口关闭被取消或失败");

                    // 隐藏窗口取消关闭后，原生句柄和 scope 依然存活，session 依然保留
                    new WindowInteropHelper(window).Handle.ShouldNotBe(IntPtr.Zero);
                    witness!.IsDisposed.ShouldBeFalse("取消关闭后 scope 必须保持存活");
                    presenter.ActiveSession.ShouldNotBeNull();
                }
                finally
                {
                    window.Closing -= cancelHandler;
                    window.ForceClose = true;
                }

                // 解除取消后重试 ShutdownAsync
                await presenter.ShutdownAsync();

                presenter.ActiveSession.ShouldBeNull();
                new WindowInteropHelper(window).Handle.ShouldBe(IntPtr.Zero);
                witness!.IsDisposed.ShouldBeTrue("重试关闭成功后 scope 最终释放");
            }
            finally
            {
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShowAsync_ConcurrentCallsWhileWaitingOldSessionCleanup_ReusesSingleNewWindow()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var queryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var queryRelease = new ManualResetEventSlim();

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var queryCount = 0;
            autoStartService.IsEnabled().Returns(_ =>
            {
                if (Interlocked.Increment(ref queryCount) == 1)
                {
                    queryEntered.TrySetResult();
                    if (!queryRelease.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Query wait timed out");
                    }
                }
                return false;
            });

            var uiDispatcher = new WpfUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            var createdWindows = new System.Collections.Generic.List<SettingsWindow>();
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
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
                createdWindows.Add(win);
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                // 首次 Show
                await presenter.ShowAsync("general");
                await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 关闭旧窗口
                createdWindows[0].Close();

                // 并发发起两次 ShowAsync，两者都等待旧会话的初始查询排干
                var firstReopen = presenter.ShowAsync("general");
                var secondReopen = presenter.ShowAsync("general");

                firstReopen.IsCompleted.ShouldBeFalse("重开任务必须等待旧会话清理");
                secondReopen.IsCompleted.ShouldBeFalse("重开任务必须等待旧会话清理");

                // 放行旧查询
                queryRelease.Set();

                await Task.WhenAll(firstReopen, secondReopen).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 验证结果：总共只创建了 2 个窗口（旧 1 + 新 1），绝不创建 3 个窗口
                createdWindows.Count.ShouldBe(2, "并发重开只能创建一个新窗口");
                createdWindows.Count(w => w.IsVisible).ShouldBe(1, "只有一个新窗口处于可见状态");
                presenter.ActiveSession.ShouldNotBeNull();
                presenter.ActiveSession!.IsOpen.ShouldBeTrue();
            }
            finally
            {
                queryRelease.Set();
                foreach (var win in createdWindows)
                {
                    win.ForceClose = true;
                    try { win.Close(); } catch { }
                }
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShowAsync_WhenShutdownCalledDuringOldSessionWait_CancelsNewWindowCreation()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var queryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var queryRelease = new ManualResetEventSlim();

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var queryCount = 0;
            autoStartService.IsEnabled().Returns(_ =>
            {
                if (Interlocked.Increment(ref queryCount) == 1)
                {
                    queryEntered.TrySetResult();
                    if (!queryRelease.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Query wait timed out");
                    }
                }
                return false;
            });

            var uiDispatcher = new WpfUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            var createdWindows = new System.Collections.Generic.List<SettingsWindow>();
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
                new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance));
            services.AddScoped<SettingsViewModel>();
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
                createdWindows.Add(win);
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                await presenter.ShowAsync("general");
                await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                createdWindows[0].Close();

                // 发起重开，此时在等旧会话排干
                var reopenTask = presenter.ShowAsync("general");

                // 在等待期间请求 Shutdown
                var shutdownTask = presenter.ShutdownAsync();

                // 放行旧查询
                queryRelease.Set();

                try
                {
                    await reopenTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                }

                await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 不应创建新窗口并保留为活跃会话
                createdWindows.Count(w => w.IsVisible).ShouldBe(0);
                presenter.ActiveSession.ShouldBeNull();
            }
            finally
            {
                queryRelease.Set();
                foreach (var win in createdWindows)
                {
                    win.ForceClose = true;
                    try { win.Close(); } catch { }
                }
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShowAsync_WhenShowAndCleanupBothFail_RetainsSession_AndPropagatesShutdownError_ThenAllowsRetry()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new WpfUiDispatcher();

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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>();

            var shouldFailShow = true;
            var shouldCancelClose = true;
            SettingsWindow? window = null;

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
                win.SourceInitialized += (_, _) =>
                {
                    if (shouldFailShow)
                    {
                        throw new InvalidOperationException("Injected Show failure.");
                    }
                };
                win.Closing += (_, e) =>
                {
                    if (shouldCancelClose)
                    {
                        e.Cancel = true;
                    }
                };
                window = win;
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                // 1. 发起 ShowAsync：窗口展示抛错，且回滚被 Closing 取消
                var showEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
                showEx.Message.ShouldContain("Injected Show failure.");

                // 2. 断言 Presenter 保留会话所有权、HWND 与 scope 存活
                presenter.ActiveSession.ShouldNotBeNull("Show 失败且回滚关闭取消时，Presenter 必须保留会话所有权");
                var session = presenter.ActiveSession!;
                session.IsOpen.ShouldBeTrue("会话必须处于存活状态");

                window.ShouldNotBeNull();
                var handle = new WindowInteropHelper(window!).Handle;
                handle.ShouldNotBe(IntPtr.Zero, "原生 HWND 句柄必须存活");
                witness.ShouldNotBeNull();
                witness!.IsDisposed.ShouldBeFalse("Scope 不得被提前释放");

                // 3. 此时调用 ShutdownAsync：因关闭仍被取消，必须向调用方传递失败，不能报告成功
                var shutdownEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShutdownAsync());
                shutdownEx.Message.ShouldContain("设置窗口关闭被取消或失败");
                presenter.ActiveSession.ShouldBe(session, "关闭失败后 Presenter 必须继续保留同一会话");
                witness.IsDisposed.ShouldBeFalse("关闭失败后 scope 必须保持存活");

                // 4. 移除故障：允许关闭与展示
                shouldFailShow = false;
                shouldCancelClose = false;

                // 5. 再次关闭：同一会话关闭且释放一次，所有权解除
                await session.ShutdownAsync();
                session.IsOpen.ShouldBeFalse();
                witness.IsDisposed.ShouldBeTrue("会话成功关闭后 scope 必须被释放");
                presenter.ActiveSession.ShouldBeNull("会话关闭后所有权必须被清除");

                // 6. 所有权解除后，允许下一次创建新会话
                await presenter.ShowAsync("general");
                presenter.ActiveSession.ShouldNotBeNull("所有权解除后允许创建新会话");
                presenter.ActiveSession.ShouldNotBe(session, "新会话必须是新的实例");
                presenter.ActiveSession!.IsOpen.ShouldBeTrue();
            }
            finally
            {
                shouldCancelClose = false;
                if (window != null)
                {
                    window.ForceClose = true;
                    try { window.Close(); } catch { }
                }
                if (presenter.ActiveSession != null)
                {
                    await presenter.ShutdownAsync();
                }
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhenWindowFactoryInProgress_AwaitsOpenTaskAndDisposesScope()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var factoryRelease = new ManualResetEventSlim();
            var factoryExited = 0;

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new WpfUiDispatcher();

            var services = new ServiceCollection();
            services.AddSingleton(settingsService);
            services.AddSingleton(autoStartService);
            services.AddSingleton<IUiDispatcher>(uiDispatcher);
            services.AddSingleton<ThemeService>();

            ScopedDisposableWitness? witness = null;
            SettingsWindow? window = null;
            services.AddScoped<ScopedDisposableWitness>(sp =>
            {
                witness = new ScopedDisposableWitness();
                return witness;
            });
            services.AddScoped<GeneralSettingsSaveCoordinator>(sp =>
            {
                _ = sp.GetRequiredService<ScopedDisposableWitness>();
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>(sp =>
            {
                witness = sp.GetRequiredService<ScopedDisposableWitness>();
                window = new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
                factoryEntered.TrySetResult();
                if (!factoryRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Factory release timed out");
                }
                Interlocked.Exchange(ref factoryExited, 1);
                return window;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            Task? showTask = null;
            Task? shutdownTask = null;

            var shutdownCoordinatorTask = Task.Run(async () =>
            {
                try
                {
                    await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // 此时窗口工厂正在执行，ActiveSession 尚未发布
                    presenter.ActiveSession.ShouldBeNull("窗口工厂进行中，ActiveSession 尚未发布");

                    shutdownTask = presenter.ShutdownAsync();

                    // 在放行工厂前，关停任务必须挂起等待，且 scope 存活
                    shutdownTask.IsCompleted.ShouldBeFalse("在窗口工厂进行中，关停任务不得提前完成");
                    Volatile.Read(ref factoryExited).ShouldBe(0, "窗口工厂仍在等待中");
                    witness.ShouldNotBeNull();
                    witness!.IsDisposed.ShouldBeFalse("窗口工厂进行中，Scope 必须存活");

                    // 放行工厂
                    factoryRelease.Set();

                    await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    factoryRelease.Set();
                }
            });

            try
            {
                showTask = Task.Run(() => presenter.ShowAsync("general"));
                try
                {
                    await showTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                }

                await shutdownCoordinatorTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                // 关停完成后，scope 必须被清理释放，无活跃会话
                witness.ShouldNotBeNull();
                witness!.IsDisposed.ShouldBeTrue("关停完成后 scope 必须被清理释放");
                presenter.ActiveSession.ShouldBeNull();
            }
            finally
            {
                factoryRelease.Set();
                await shutdownCoordinatorTask;
                if (showTask != null) { try { await showTask; } catch { } }
                if (window != null)
                {
                    window.ForceClose = true;
                    try { window.Close(); } catch { }
                }
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShutdownAsync_WhenShowQueuedInDispatcherBeforeWindowCreation_AwaitsOpenTaskAndDoesNotLeak()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gateRelease = new ManualResetEventSlim();
            var factoryCalls = 0;

            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new WpfUiDispatcher();

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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });
            services.AddScoped<SettingsViewModel>();
            services.AddTransient<SettingsWindow>(sp =>
            {
                Interlocked.Increment(ref factoryCalls);
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

            Task? showTask = null;
            Task? shutdownTask = null;

            var raceTask = Task.Run(async () =>
            {
                try
                {
                    await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // 在后台调用 ShowAsync，派发创建任务入 Dispatcher 队列
                    showTask = Task.Run(() => presenter.ShowAsync("general"));

                    while (presenter.OpenTask == null)
                    {
                        await Task.Yield();
                    }

                    // 此时创建委托在 Dispatcher 队列中排队，尚未调用窗口工厂
                    factoryCalls.ShouldBe(0, "ShowCoreAsync 仍在 Dispatcher 队列中排队，尚未调用窗口工厂");

                    // 发起关停：由于 _openTask 排队进行中，关停任务处于挂起状态
                    shutdownTask = presenter.ShutdownAsync();
                    shutdownTask.IsCompleted.ShouldBeFalse("创建委托在排队中时，关停任务必须挂起等待");

                    // 放行 UI Dispatcher
                    gateRelease.Set();

                    try
                    {
                        await showTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5));

                    // 验证结果：窗口工厂自始至终未被调用，会话为空
                    factoryCalls.ShouldBe(0, "关停取消了排队中的创建，未调用窗口工厂");
                    presenter.ActiveSession.ShouldBeNull();
                }
                finally
                {
                    gateRelease.Set();
                }
            });

            try
            {
                gateEntered.TrySetResult();
                if (!gateRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Gate release timed out");
                }

                await raceTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            finally
            {
                gateRelease.Set();
                await raceTask;
                if (showTask != null) { try { await showTask; } catch { } }
                if (shutdownTask != null) { try { await shutdownTask; } catch { } }
                await presenter.ShutdownAsync();
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShowAsync_WhenPartialRollbackClosingCancelled_RetainsWindowAndScope_AndShutdownRecoversAfterUncancel(bool createHandle)
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new WpfUiDispatcher();

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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });

            // 注入 ViewModel 解析失败（模拟未组装 Session 阶段的局部回滚）
            services.AddScoped<SettingsViewModel>(sp => throw new InvalidOperationException("Injected ViewModel resolution failure."));

            SettingsWindow? window = null;
            System.ComponentModel.CancelEventHandler cancelHandler = (_, e) => e.Cancel = true;

            services.AddTransient<SettingsWindow>(sp =>
            {
                witness = sp.GetRequiredService<ScopedDisposableWitness>();
                var win = new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
                win.Closing += cancelHandler;
                if (createHandle)
                {
                    new WindowInteropHelper(win).EnsureHandle();
                }
                window = win;
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                // 1. 发起展示：ViewModel 解析抛错，进入尚未组装 Session 的局部回滚分支，但 Close 被 Closing 取消
                var showEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
                showEx.Message.ShouldContain("Injected ViewModel resolution failure.");

                window.ShouldNotBeNull();
                witness.ShouldNotBeNull();

                // 2. 故障存在时：断言窗口仍存活，scope 未释放，Presenter 保留清理责任
                Application.Current.Windows.Cast<Window>().Contains(window!).ShouldBeTrue("取消关闭后窗口必须保持存活在 Application.Windows");
                witness!.IsDisposed.ShouldBeFalse("窗口尚未成功关闭前 scope 绝对不得被提前释放");
                presenter.HasPendingRollback.ShouldBeTrue("Presenter 必须保留局部回滚的所有权与重试入口");

                if (createHandle)
                {
                    new WindowInteropHelper(window!).Handle.ShouldNotBe(IntPtr.Zero);
                }

                // 3. 故障存在时调用 ShutdownAsync：因关闭取消，必须向调用方传递失败，不能提前报告成功
                var shutdownEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShutdownAsync());
                shutdownEx.Message.ShouldContain("设置窗口关闭被取消或失败");
                witness.IsDisposed.ShouldBeFalse("关停失败后 scope 必须继续存活");
                presenter.HasPendingRollback.ShouldBeTrue("关停失败后 Presenter 必须继续保留待清理资源");

                // 4. 移除故障：允许窗口正常关闭
                window!.Closing -= cancelHandler;

                // 5. 解除取消后再次 Shutdown：断言窗口从 Application.Windows 移除，HWND 不再存活，scope 释放一次
                await presenter.ShutdownAsync();

                Application.Current.Windows.Cast<Window>().Contains(window).ShouldBeFalse("重试成功后窗口必须从 Application.Windows 移除");
                new WindowInteropHelper(window).Handle.ShouldBe(IntPtr.Zero, "重试成功后窗口原生句柄必须被销毁");
                witness.IsDisposed.ShouldBeTrue("重试成功后 scope 必须被正确释放");
                presenter.HasPendingRollback.ShouldBeFalse("清理完成后待回滚状态必须已清空");
            }
            finally
            {
                if (window != null)
                {
                    window.Closing -= cancelHandler;
                    window.ForceClose = true;
                    try { window.Close(); } catch { }
                }
                await presenter.ShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShowAsync_WhenPartialRollbackSucceedsNormally_DisposesScopeAndCleansUpWindow()
    {
        await WpfTestRunner.RunAsync(async () =>
        {
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Current.Returns(new AppSettings());

            var autoStartService = Substitute.For<IAutoStartService>();
            var uiDispatcher = new WpfUiDispatcher();

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
                return new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);
            });

            // 注入 ViewModel 解析失败（模拟未组装 Session 阶段的局部回滚）
            services.AddScoped<SettingsViewModel>(sp => throw new InvalidOperationException("Injected ViewModel resolution failure."));

            SettingsWindow? window = null;

            services.AddTransient<SettingsWindow>(sp =>
            {
                witness = sp.GetRequiredService<ScopedDisposableWitness>();
                var win = new SettingsWindow
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    Opacity = 0
                };
                window = win;
                return win;
            });

            using var rootProvider = services.BuildServiceProvider();
            var presenter = new SettingsWindowPresenter(rootProvider, uiDispatcher, NullLogger<SettingsWindowPresenter>.Instance);

            try
            {
                // 发起展示：ViewModel 解析抛错，正常执行局部回滚（未取消关闭）
                var showEx = await Should.ThrowAsync<InvalidOperationException>(() => presenter.ShowAsync("general"));
                showEx.Message.ShouldContain("Injected ViewModel resolution failure.");

                window.ShouldNotBeNull();
                witness.ShouldNotBeNull();

                // 断言无 Closing 取消的正常失败回滚：窗口关闭且从 Application.Windows 移除，scope 释放，Presenter 干净
                Application.Current.Windows.Cast<Window>().Contains(window!).ShouldBeFalse("正常失败回滚必须关闭窗口并脱离 Application.Windows");
                witness!.IsDisposed.ShouldBeTrue("正常失败回滚必须释放 scope");
                presenter.HasPendingRollback.ShouldBeFalse("正常失败回滚后 Presenter 无残留待清理项");
                presenter.ActiveSession.ShouldBeNull();
            }
            finally
            {
                if (window != null)
                {
                    window.ForceClose = true;
                    try { window.Close(); } catch { }
                }
                await presenter.ShutdownAsync();
            }
        });
    }
}
