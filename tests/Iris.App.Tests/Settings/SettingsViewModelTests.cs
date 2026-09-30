using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsViewModelTests
{
    private static (SettingsViewModel ViewModel, ISettingsService SettingsService, ThemeService ThemeService, GeneralSettingsSaveCoordinator Coordinator, IAutoStartService AutoStart) CreateSut(AppSettings? initialSettings = null)
    {
        var settings = initialSettings ?? new AppSettings
        {
            General = new GeneralSettings
            {
                Theme = AppTheme.Light,
                LaunchAtStartup = false,
                ShowStartupNotification = true,
                LogLevel = "Information"
            }
        };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var themeService = new ThemeService(settingsService);
        var autoStartService = Substitute.For<IAutoStartService>();
        var coordinator = new GeneralSettingsSaveCoordinator(settingsService, autoStartService, NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var uiDispatcher = Substitute.For<IUiDispatcher>();
        uiDispatcher.InvokeAsync(Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var fn = callInfo.Arg<Func<Task>>();
                return fn();
            });

        var vm = new SettingsViewModel(settingsService, themeService, coordinator, uiDispatcher);
        return (vm, settingsService, themeService, coordinator, autoStartService);
    }

    [Fact]
    public void Initialization_LoadsValuesFromCurrentSettings()
    {
        var (vm, _, _, _, _) = CreateSut();
        using (vm)
        {
            vm.Theme.ShouldBe(AppTheme.Light);
            vm.LaunchAtStartup.ShouldBeFalse();
            vm.ShowStartupNotification.ShouldBeTrue();
            vm.LogLevel.ShouldBe("Information");
            vm.IsDirty.ShouldBeFalse();
            vm.IsSaving.ShouldBeFalse();
            vm.IsConflict.ShouldBeFalse();
            vm.CanRetryAutoStart.ShouldBeFalse();
            vm.StatusMessage.ShouldBeNull();

            SettingsViewModel.AvailableThemes.ShouldContain(AppTheme.System);
            SettingsViewModel.AvailableThemes.ShouldContain(AppTheme.Light);
            SettingsViewModel.AvailableThemes.ShouldContain(AppTheme.Dark);

            SettingsViewModel.AvailableLogLevels.ShouldContain("Information");
        }
    }

    [Fact]
    public void ModifyingProperty_MarksIsDirty_AndThemePreviewTriggers()
    {
        var (vm, _, themeService, _, _) = CreateSut();
        using (vm)
        {
            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();
            themeService.HasActivePreview.ShouldBeTrue();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

            // 还原回原始值，IsDirty 变回 false
            vm.Theme = AppTheme.Light;
            vm.IsDirty.ShouldBeFalse();
        }
    }

    [Fact]
    public void ExecuteCancel_ClearsPreview_AndRaisesRequestClose()
    {
        var (vm, _, themeService, _, _) = CreateSut();
        using (vm)
        {
            var closeRequested = false;
            vm.RequestClose += () => closeRequested = true;

            vm.Theme = AppTheme.Dark;
            themeService.HasActivePreview.ShouldBeTrue();

            vm.CancelCommand.Execute(null);

            closeRequested.ShouldBeTrue();
            themeService.HasActivePreview.ShouldBeFalse();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Light);
        }
    }

    [Fact]
    public void ExecuteReload_ResetsDraftToBaseSnapshot_AndClearsDirtyAndStatus()
    {
        var (vm, _, themeService, _, _) = CreateSut();
        using (vm)
        {
            vm.Theme = AppTheme.Dark;
            vm.LaunchAtStartup = true;
            vm.ShowStartupNotification = false;
            vm.LogLevel = "Debug";
            vm.IsDirty.ShouldBeTrue();

            vm.ReloadCommand.Execute(null);

            vm.Theme.ShouldBe(AppTheme.Light);
            vm.LaunchAtStartup.ShouldBeFalse();
            vm.ShowStartupNotification.ShouldBeTrue();
            vm.LogLevel.ShouldBe("Information");
            vm.IsDirty.ShouldBeFalse();
            themeService.HasActivePreview.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ExecuteSaveAsync_WhenSuccess_ClearsPreview_UpdatesBaseSnapshot_AndSetsStatus()
    {
        var (vm, settingsService, themeService, _, _) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    return Task.CompletedTask;
                });

            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();

            await vm.ExecuteSaveAsync();

            vm.IsDirty.ShouldBeFalse();
            vm.StatusMessage.ShouldBe("设置已保存");
            vm.IsConflict.ShouldBeFalse();
            themeService.HasActivePreview.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ExecuteSaveAsync_WhenConflict_PreservesDraft_SetsConflictMessage_AndIsConflictTrue()
    {
        var (vm, settingsService, _, _, _) = CreateSut();
        using (vm)
        {
            // 外部更新导致保存时冲突检测触发
            var externalSettings = new AppSettings
            {
                General = new GeneralSettings { Theme = AppTheme.System }
            };
            settingsService.Current.Returns(externalSettings);

            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    mutate(externalSettings); // 抛出冲突
                    return Task.CompletedTask;
                });

            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();

            await vm.ExecuteSaveAsync();

            vm.IsDirty.ShouldBeTrue();
            vm.IsConflict.ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("冲突");
        }
    }

    [Fact]
    public async Task ExecuteSaveAsync_WhenAutoStartPending_UpdatesBase_SetsSavedApplicationPending_AndEnablesRetry()
    {
        var (vm, settingsService, _, _, autoStart) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    return Task.CompletedTask;
                });

            autoStart.When(x => x.SetEnabled(true)).Do(_ => throw new InvalidOperationException("Registry permission denied"));

            vm.LaunchAtStartup = true;
            vm.IsDirty.ShouldBeTrue();

            await vm.ExecuteSaveAsync();

            vm.IsDirty.ShouldBeFalse();
            vm.CanRetryAutoStart.ShouldBeTrue();
            vm.RetryAutoStartCommand.CanExecute(null).ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("开机启动设置未生效");
        }
    }

    [Fact]
    public void ExternalSettingsChanged_WhenFieldNotDirty_FollowsLatestSettings()
    {
        var (vm, settingsService, _, _, _) = CreateSut();
        using (vm)
        {
            // 用户只编辑了 Theme
            vm.Theme = AppTheme.Dark;

            // 外部修改了 LogLevel
            var oldSettings = settingsService.Current;
            var updatedSettings = oldSettings with
            {
                General = oldSettings.General with { LogLevel = "Warning" }
            };

            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService,
                new SettingsChangedEventArgs(oldSettings, updatedSettings));

            // 未编辑的 LogLevel 自动跟随最新值
            vm.LogLevel.ShouldBe("Warning");
            // 已编辑的 Theme 保持用户的目标 Dark
            vm.Theme.ShouldBe(AppTheme.Dark);
            vm.IsDirty.ShouldBeTrue();
        }
    }
}
