using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

using System.Collections.Concurrent;

namespace Iris.App.Tests.Settings;

public sealed class SettingsWindowPresenterTests
{
    private static class StaRunner
    {
        private static readonly BlockingCollection<(Func<Task> Work, TaskCompletionSource Tcs)> Queue = new();

        static StaRunner()
        {
            var thread = new Thread(ProcessQueue)
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static void ProcessQueue()
        {
            EnsureApplication();
            while (!Queue.IsCompleted)
            {
                try
                {
                    var item = Queue.Take();
                    try
                    {
                        item.Work().GetAwaiter().GetResult();
                        item.Tcs.SetResult();
                    }
                    catch (Exception ex)
                    {
                        item.Tcs.SetException(ex);
                    }
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }

        public static async Task RunAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add((work, tcs));
            await tcs.Task;
        }
    }

    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            return callback();
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
    public async Task ShowAsync_InStaThread_CreatesSession_BindsViewModel_ReusesActiveWindow_AndCleansUpOnClose()
    {
        await StaRunner.RunAsync(async () =>
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
        await StaRunner.RunAsync(async () =>
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
}
