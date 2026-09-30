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
            settingsService.Current.Returns(updatedSettings);

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

    [Fact]
    public async Task EditDuringSave_IsDisabled_AndLateEditsAreIgnored()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (vm, settingsService, _, _, _) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    saveStarted.TrySetResult();
                    await saveRelease.Task;
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                });

            vm.LogLevel = "Debug";
            vm.IsDirty.ShouldBeTrue();

            var saveTask = vm.ExecuteSaveAsync();
            await saveStarted.Task;

            // 保存中状态
            vm.IsSaving.ShouldBeTrue();
            vm.CanEdit.ShouldBeFalse();

            // 尝试在保存中修改各字段
            vm.LogLevel = "Error";
            vm.Theme = AppTheme.Dark;
            vm.LaunchAtStartup = true;
            vm.ShowStartupNotification = false;

            // 验证未被更改
            vm.LogLevel.ShouldBe("Debug");
            vm.Theme.ShouldBe(AppTheme.Light);
            vm.LaunchAtStartup.ShouldBeFalse();
            vm.ShowStartupNotification.ShouldBeTrue();

            saveRelease.TrySetResult();
            await saveTask;

            vm.IsSaving.ShouldBeFalse();
            vm.CanEdit.ShouldBeTrue();
            vm.LogLevel.ShouldBe("Debug");
            vm.IsDirty.ShouldBeFalse();
            settingsService.Current.General.LogLevel.ShouldBe("Debug");
        }
    }

    [Fact]
    public async Task ExternalSettingsChanged_DuringSave_UpdatesBaselineFromCurrent_WhenSaveFinishes()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var saveRelease = new ManualResetEventSlim();

        var (vm, settingsService, _, _, autoStart) = CreateSut();
        using (vm)
        {
            settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                    var updated = mutate(settingsService.Current);
                    settingsService.Current.Returns(updated);
                    await Task.CompletedTask;
                });

            autoStart.When(x => x.SetEnabled(true)).Do(_ =>
            {
                saveStarted.TrySetResult();
                if (!saveRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Save release wait timed out");
                }
            });

            vm.LogLevel = "Debug";
            vm.LaunchAtStartup = true;

            var saveTask = vm.ExecuteSaveAsync();
            await saveStarted.Task;

            // 外部更新将已持久化的 LogLevel 改为 Error
            var externalSettings = settingsService.Current with
            {
                General = settingsService.Current.General with { LogLevel = "Error" }
            };
            settingsService.Current.Returns(externalSettings);

            saveRelease.Set();
            await saveTask;

            // 保存完成后从 Current 建立基线，UI 同步反映真实状态
            vm.LogLevel.ShouldBe("Error");
            vm.IsDirty.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task SaveAsync_WhenAutoStartApplyFails_PreservesRetryEligibility_AcrossUnrelatedSavesAndReload()
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

            // 模拟自启注册失败
            var failAutoStart = true;
            autoStart.When(x => x.SetEnabled(true)).Do(_ =>
            {
                if (failAutoStart)
                {
                    throw new InvalidOperationException("Registry error");
                }
                autoStart.IsEnabled().Returns(true);
            });
            autoStart.IsEnabled().Returns(_ => !failAutoStart);

            vm.LaunchAtStartup = true;
            await vm.ExecuteSaveAsync();

            vm.CanRetryAutoStart.ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("开机启动设置未生效");

            // 保存无关字段（如 LogLevel）
            vm.LogLevel = "Debug";
            await vm.ExecuteSaveAsync();

            // 自启仍待应用，重试资格独立保留
            vm.CanRetryAutoStart.ShouldBeTrue();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("开机启动设置仍未生效");

            // 执行 Reload，重试资格依然保留
            vm.ExecuteReload();
            vm.CanRetryAutoStart.ShouldBeTrue();

            // 重试成功
            failAutoStart = false;
            await vm.ExecuteRetryAutoStartAsync();

            vm.CanRetryAutoStart.ShouldBeFalse();
            vm.StatusMessage.ShouldNotBeNull();
            vm.StatusMessage!.ShouldContain("已生效");
        }
    }

    [Fact]
    public void Theme_WhenChangedAndChangedBackToBase_ClearsPreview_AndFollowsExternalUpdates()
    {
        var (vm, settingsService, themeService, _, _) = CreateSut();
        using (vm)
        {
            // 初始持久主题为 Light
            themeService.EffectiveTheme.ShouldBe(AppTheme.Light);
            themeService.HasActivePreview.ShouldBeFalse();

            // 改为 Dark：触发预览
            vm.Theme = AppTheme.Dark;
            vm.IsDirty.ShouldBeTrue();
            themeService.HasActivePreview.ShouldBeTrue();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

            // 改回 Light：结束预览，回到基线
            vm.Theme = AppTheme.Light;
            vm.IsDirty.ShouldBeFalse();
            themeService.HasActivePreview.ShouldBeFalse();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Light);

            // 外部更新持久主题为 Dark
            var oldSettings = settingsService.Current;
            var newSettings = oldSettings with
            {
                General = oldSettings.General with { Theme = AppTheme.Dark }
            };
            settingsService.Current.Returns(newSettings);

            settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
                settingsService,
                new SettingsChangedEventArgs(oldSettings, newSettings));

            // UI 与 EffectiveTheme 一致更新为 Dark，且无悬挂预览
            vm.Theme.ShouldBe(AppTheme.Dark);
            themeService.HasActivePreview.ShouldBeFalse();
            themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);
        }
    }
}
