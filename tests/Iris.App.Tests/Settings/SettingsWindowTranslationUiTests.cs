using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
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

public sealed class SettingsWindowTranslationUiTests
{
    private static ServiceProvider CreateServiceProvider(AppSettings? initialSettings = null)
    {
        var settings = initialSettings ?? new AppSettings();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(settings);

        var autoStartService = Substitute.For<IAutoStartService>();
        var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
        var receiptManager = new SettingsApplyReceiptManager();
        var uiDispatcher = new ImmediateUiDispatcher();

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(autoStartService);
        services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);
        services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<ThemeService>();

        services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            receiptManager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance));

        services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
            settingsService,
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
            editCoordinator,
            receiptManager,
            uiDispatcher,
            NullLogger<SettingsViewModel>.Instance));

        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task WindowDimensions_MatchesDefaultAndMinimumSpecifications()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            var window = provider.GetRequiredService<SettingsWindow>();

            window.Width.ShouldBe(760);
            window.Height.ShouldBe(520);
            window.MinWidth.ShouldBe(600);
            window.MinHeight.ShouldBe(440);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TwoPageNavigation_SwitchesBetweenGeneralAndTranslation()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            window.DataContext = vm;

            vm.CurrentPageKey.ShouldBe("general");

            // 切换至 translation
            vm.CurrentPageKey = "translation";
            vm.CurrentPageKey.ShouldBe("translation");

            // 切换回 general
            vm.CurrentPageKey = "general";
            vm.CurrentPageKey.ShouldBe("general");

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PresetsSelection_AddsPresetToProfilesList_WithCorrectDefaults()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();

            var deepseekPreset = SettingsViewModel.AvailablePresets.FirstOrDefault(p => p.DisplayName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase));
            deepseekPreset.ShouldNotBeNull();

            vm.AddProfileFromPreset(deepseekPreset!);

            vm.Profiles.Count.ShouldBe(1);
            var draft = vm.Profiles[0];
            draft.DisplayName.ShouldBe("DeepSeek");
            draft.Kind.ShouldBe(TranslationProviderKind.OpenAICompatible);
            draft.BaseUrl.ShouldBe("https://api.deepseek.com");
            draft.Model.ShouldBe(string.Empty);
            draft.IsNew.ShouldBeTrue();
            vm.IsDirty.ShouldBeTrue();
            vm.SelectedProfile.ShouldBe(draft);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ProfileSelection_ClearsApiKeyPasswordBox()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            window.DataContext = vm;

            // 添加两个 Profile
            var p1 = SettingsViewModel.AvailablePresets[0];
            var p2 = SettingsViewModel.AvailablePresets[1];
            vm.AddProfileFromPreset(p1);
            vm.AddProfileFromPreset(p2);

            vm.Profiles.Count.ShouldBe(2);

            // 选中第一个 Profile 并设置密码框（选择 Replace 意图）
            vm.SelectedProfile = vm.Profiles[0];
            vm.SelectedProfile.CredentialEditKind = CredentialEditKind.Replace;
            var pb = window.FindName("ApiKeyPasswordBox") as PasswordBox;
            pb.ShouldNotBeNull();
            pb!.Password = "sk-secret-test-key-1";

            vm.SelectedProfile.BuildCredentialEdit().Kind.ShouldBe(CredentialEditKind.Replace);

            // 切换到第二个 Profile
            vm.SelectedProfile = vm.Profiles[1];

            // 密码框内容必须被清空，不串给第二个 Profile
            pb.Password.ShouldBe(string.Empty);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ProfileReordering_MoveUpAndMoveDown_ReordersItemsAndMarksDirty()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();

            var p1 = SettingsViewModel.AvailablePresets[0];
            var p2 = SettingsViewModel.AvailablePresets[1];
            vm.AddProfileFromPreset(p1);
            vm.AddProfileFromPreset(p2);

            var firstId = vm.Profiles[0].Id;
            var secondId = vm.Profiles[1].Id;

            // 选中第 0 项，下移
            vm.SelectedProfile = vm.Profiles[0];
            vm.ExecuteMoveDown();

            vm.Profiles[0].Id.ShouldBe(secondId);
            vm.Profiles[1].Id.ShouldBe(firstId);

            // 选中第 1 项，上移
            vm.ExecuteMoveUp();

            vm.Profiles[0].Id.ShouldBe(firstId);
            vm.Profiles[1].Id.ShouldBe(secondId);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UnfocusedInput_FlushesFocusedBinding_BeforeSave()
    {
        await WpfTestRunner.RunAsync(() =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            window.DataContext = vm;

            vm.AddProfileFromPreset(SettingsViewModel.AvailablePresets[0]);
            var draft = vm.SelectedProfile;
            draft.ShouldNotBeNull();

            // 构造未失焦的 TextBox 模拟输入
            var tb = new TextBox();
            var binding = new Binding("DisplayName")
            {
                Source = draft,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.Explicit
            };
            BindingOperations.SetBinding(tb, TextBox.TextProperty, binding);

            tb.Text = "Unfocused Updated Name";
            // Explicit trigger 下，尚未 UpdateSource
            draft!.DisplayName.ShouldNotBe("Unfocused Updated Name");

            // 模拟将该 TextBox 视为焦点元素并刷新
            BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();

            // 验证未失焦输入成功刷入草稿
            draft.DisplayName.ShouldBe("Unfocused Updated Name");

            return Task.CompletedTask;
        });
    }
}
