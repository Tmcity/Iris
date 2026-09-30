using System;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Services;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Services;

public sealed class GeneralSettingsSaveCoordinatorTests
{
    [Fact]
    public async Task SaveAsync_WhenNotDirty_ReturnsSavedAndApplied_DoesNotWriteSettingsOrCallAutoStart()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var autoStartService = Substitute.For<IAutoStartService>();
        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var snapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: AppTheme.Light,
            ThemeDirty: false,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Information",
            LogLevelDirty: false);

        var result = await coordinator.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(GeneralSaveStatus.SavedAndApplied);
        await settingsService.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
        autoStartService.DidNotReceiveWithAnyArgs().SetEnabled(Arg.Any<bool>());
    }

    [Fact]
    public async Task SaveAsync_WhenDirty_UpdatesOnlyDirtyFields_AndPreservesUneditedAndHiddenFields()
    {
        var initialSettings = new AppSettings
        {
            General = new GeneralSettings
            {
                UiLanguage = "zh-CN",
                CheckForUpdates = false,
                Theme = AppTheme.Light,
                LaunchAtStartup = false,
                ShowStartupNotification = true,
                LogLevel = "Information"
            }
        };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(initialSettings);
        settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                var updated = mutate(initialSettings);
                settingsService.Current.Returns(updated);
                return Task.CompletedTask;
            });

        var autoStartService = Substitute.For<IAutoStartService>();
        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        // 仅修改 Theme 为 Dark
        var snapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: AppTheme.Dark,
            ThemeDirty: true,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Information",
            LogLevelDirty: false);

        var result = await coordinator.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(GeneralSaveStatus.SavedAndApplied);
        settingsService.Current.General.Theme.ShouldBe(AppTheme.Dark);
        // 验证未编辑字段和隐藏字段均得到完整保留
        settingsService.Current.General.UiLanguage.ShouldBe("zh-CN");
        settingsService.Current.General.CheckForUpdates.ShouldBeFalse();
        settingsService.Current.General.LaunchAtStartup.ShouldBeFalse();
        settingsService.Current.General.ShowStartupNotification.ShouldBeTrue();
        settingsService.Current.General.LogLevel.ShouldBe("Information");
        autoStartService.DidNotReceiveWithAnyArgs().SetEnabled(Arg.Any<bool>());
    }

    [Fact]
    public async Task SaveAsync_WhenSameFieldModifiedExternally_ReturnsUncommittedWithConflict()
    {
        var currentExternalSettings = new AppSettings
        {
            General = new GeneralSettings
            {
                Theme = AppTheme.System, // 外部已被改成了 System
                LaunchAtStartup = false,
                ShowStartupNotification = true,
                LogLevel = "Information"
            }
        };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(currentExternalSettings);
        settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                // mutate 内部检测到 current.General.Theme (System) != BaseTheme (Light) 且 != TargetTheme (Dark)
                mutate(currentExternalSettings);
                return Task.CompletedTask;
            });

        var autoStartService = Substitute.For<IAutoStartService>();
        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        // 本地草稿：基线 Light，目标 Dark
        var snapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: AppTheme.Dark,
            ThemeDirty: true,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Information",
            LogLevelDirty: false);

        var result = await coordinator.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
        result.IsConflict.ShouldBeTrue();
        autoStartService.DidNotReceiveWithAnyArgs().SetEnabled(Arg.Any<bool>());
    }

    [Fact]
    public async Task SaveAsync_WhenInvalidThemeOrLogLevel_ReturnsUncommitted_WithoutCallingUpdateAsync()
    {
        var settingsService = Substitute.For<ISettingsService>();
        var autoStartService = Substitute.For<IAutoStartService>();
        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        // 无效主题
        var invalidThemeSnapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: (AppTheme)999,
            ThemeDirty: true,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Information",
            LogLevelDirty: false);

        var result1 = await coordinator.SaveAsync(invalidThemeSnapshot, TestContext.Current.CancellationToken);
        result1.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
        result1.FailureReason.ShouldNotBeNull();
        result1.FailureReason!.ShouldContain("无效的主题");

        // 无效日志级别
        var invalidLogLevelSnapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: AppTheme.Light,
            ThemeDirty: false,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "NonExistentLogLevel",
            LogLevelDirty: true);

        var result2 = await coordinator.SaveAsync(invalidLogLevelSnapshot, TestContext.Current.CancellationToken);
        result2.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
        result2.FailureReason.ShouldNotBeNull();
        result2.FailureReason!.ShouldContain("无效的日志级别");

        await settingsService.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_WhenAutoStartThrows_ReturnsSavedApplicationPending_PreservingSavedSettings()
    {
        var initialSettings = new AppSettings
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
        settingsService.Current.Returns(initialSettings);
        settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var mutate = callInfo.Arg<Func<AppSettings, AppSettings>>();
                var updated = mutate(initialSettings);
                settingsService.Current.Returns(updated);
                return Task.CompletedTask;
            });

        var autoStartService = Substitute.For<IAutoStartService>();
        autoStartService.When(x => x.SetEnabled(true)).Do(_ => throw new InvalidOperationException("Registry error"));

        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var snapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: AppTheme.Light,
            ThemeDirty: false,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: true,
            LaunchAtStartupDirty: true,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Information",
            LogLevelDirty: false);

        var result = await coordinator.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(GeneralSaveStatus.SavedApplicationPending);
        result.FailureReason.ShouldNotBeNull();
        result.FailureReason!.ShouldContain("开机启动设置未生效");
        settingsService.Current.General.LaunchAtStartup.ShouldBeTrue();
    }

    [Fact]
    public async Task RetryAutoStartAsync_AppliesCurrentLaunchAtStartup_WithoutCallingUpdateAsync()
    {
        var currentSettings = new AppSettings
        {
            General = new GeneralSettings
            {
                LaunchAtStartup = true
            }
        };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(currentSettings);

        var autoStartService = Substitute.For<IAutoStartService>();

        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var result = await coordinator.RetryAutoStartAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(GeneralSaveStatus.SavedAndApplied);
        autoStartService.Received(1).SetEnabled(true);
        await settingsService.DidNotReceiveWithAnyArgs().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_WhenCancelledBeforeCommit_ReturnsUncommitted()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromCanceled(new CancellationToken(true)));

        var autoStartService = Substitute.For<IAutoStartService>();
        using var coordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            autoStartService,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var snapshot = new GeneralDraftSnapshot(
            BaseTheme: AppTheme.Light,
            TargetTheme: AppTheme.Dark,
            ThemeDirty: true,
            BaseLaunchAtStartup: false,
            TargetLaunchAtStartup: false,
            LaunchAtStartupDirty: false,
            BaseShowStartupNotification: true,
            TargetShowStartupNotification: true,
            ShowStartupNotificationDirty: false,
            BaseLogLevel: "Information",
            TargetLogLevel: "Information",
            LogLevelDirty: false);

        var result = await coordinator.SaveAsync(snapshot, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
        result.FailureReason.ShouldNotBeNull();
        result.FailureReason!.ShouldContain("取消");
    }
}
