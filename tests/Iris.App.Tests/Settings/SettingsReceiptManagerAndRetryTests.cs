using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

public sealed class SettingsReceiptManagerAndRetryTests
{
    private sealed class FakeReceipt(Guid id) : ISettingsApplyReceipt
    {
        public Guid CommitId => id;
    }

    [Fact]
    public void ReceiptManager_EnforcesMaximum32Slots_AndReleasesCorrectly()
    {
        var manager = new SettingsApplyReceiptManager();

        // 预留 32 个槽位
        for (int i = 0; i < 32; i++)
        {
            manager.TryReserveSlot().ShouldBeTrue();
        }

        // 第 33 个必须拒绝
        manager.TryReserveSlot().ShouldBeFalse();

        // 释放一个
        manager.ReleaseReservation();

        // 此时可再次预留
        manager.TryReserveSlot().ShouldBeTrue();
    }

    [Fact]
    public async Task Save_WhenQuotaFull_DoesNotCallCoreCoordinator_PreservesDraft_AndSetsQuotaFullMessage()
    {
        var manager = new SettingsApplyReceiptManager();
        // 填满 32 个槽位
        for (int i = 0; i < 32; i++)
        {
            manager.TryReserveSlot();
        }

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
        var autoStartService = Substitute.For<IAutoStartService>();

        using var saveCoordinator = new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            manager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        var newProfile = new TranslationProviderProfile
        {
            Id = "new-p",
            Kind = TranslationProviderKind.OpenAICompatible,
            DisplayName = "New P",
            BaseUrl = "https://api.test.com",
            Model = "test-model",
            Order = 0,
        };

        var plan = new SettingsEditPlan(
            new GeneralSettingsPatch(),
            [new TranslationProfileChange(TranslationProfileChangeKind.Add, null, newProfile, new ProviderCredentialEdit(CredentialEditKind.Keep))]);

        var result = await saveCoordinator.CommitPlanAsync(plan, false, false, CancellationToken.None);

        result.Status.ShouldBe(GeneralSaveStatus.Uncommitted);
        result.IsQuotaFull.ShouldBeTrue();
        result.FailureReason.ShouldBe(UiText.SettingsStatusPendingApplyQuotaFull);

        // 确认没有调用 Core 协调器的 CommitAsync
        await editCoordinator.DidNotReceiveWithAnyArgs().CommitAsync(Arg.Any<SettingsEditPlan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiptManager_RetainsPendingReceiptsAcrossWindowSessions_AndSupportsIndependentRetries()
    {
        var manager = new SettingsApplyReceiptManager();
        var fakeReceipt = new FakeReceipt(Guid.NewGuid());
        manager.TryReserveSlot().ShouldBeTrue();
        manager.ConvertReservationToPending(fakeReceipt, "Simulated network timeout during apply");

        manager.HasPendingReceipts.ShouldBeTrue();
        manager.PendingCount.ShouldBe(1);

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
        editCoordinator.RetryApplyAsync(fakeReceipt, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SettingsApplyResult(true)));

        var autoStartService = Substitute.For<IAutoStartService>();
        autoStartService.IsEnabled().Returns(false);

        var uiDispatcher = new ImmediateUiDispatcher();
        using var themeService = new ThemeService(settingsService);

        // 模拟第 1 次窗口会话
        using var coord1 = new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            manager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        using (var vm1 = new SettingsViewModel(settingsService, themeService, coord1, editCoordinator, manager, uiDispatcher))
        {
            // 打开窗口即可看到有待重试项
            vm1.CanRetryPendingApply.ShouldBeTrue();
            // 关闭第 1 个窗口（不执行重试）
        }

        // 进程级管理器仍然保留待处理回执
        manager.HasPendingReceipts.ShouldBeTrue();

        // 模拟第 2 次窗口会话打开
        using var coord2 = new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            manager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance);

        using (var vm2 = new SettingsViewModel(settingsService, themeService, coord2, editCoordinator, manager, uiDispatcher))
        {
            vm2.CanRetryPendingApply.ShouldBeTrue();

            // 执行“重试服务应用”
            await vm2.ExecuteRetryPendingApplyAsync();

            vm2.CanRetryPendingApply.ShouldBeFalse();
            vm2.StatusMessage.ShouldBe(UiText.SettingsStatusPendingApplyCompleted);
        }

        // 此时管理器中的回执已被成功移除
        manager.HasPendingReceipts.ShouldBeFalse();
    }
}
