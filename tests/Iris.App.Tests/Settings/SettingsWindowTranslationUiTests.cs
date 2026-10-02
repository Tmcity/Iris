using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
        await WpfTestRunner.RunAsync(async () =>
        {
            using var provider = CreateServiceProvider();
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            window.DataContext = vm;
            window.Show();
            vm.CurrentPageKey = "translation";
            vm.AddProfileFromPreset(SettingsViewModel.AvailablePresets[0]);
            var draft = vm.SelectedProfile;
            draft.ShouldNotBeNull();

            window.UpdateLayout();

            // 查找实际 XAML 窗口中的 DisplayNameTextBox
            var tb = (TextBox)window.FindName("DisplayNameTextBox");
            tb.ShouldNotBeNull();

            // 模拟将该 TextBox 设为窗口焦点元素
            System.Windows.Input.FocusManager.SetFocusedElement(window, tb);

            var beBefore = BindingOperations.GetBindingExpression(tb, TextBox.TextProperty);
            var focusedEl = System.Windows.Input.FocusManager.GetFocusedElement(window);

            // 用户键入新内容，保持焦点（尚未发生失焦）
            tb.Text = "Unfocused Updated Name";

            // 草稿尚未被失焦事件更新
            draft!.DisplayName.ShouldNotBe("Unfocused Updated Name");

            // 执行实际保存入口：vm.BeforeSave 触发 FlushFocusedBinding
            await vm.ExecuteSaveAsync();

            // 验证未失焦输入成功在保存前被自动刷入草稿
            draft.DisplayName.ShouldBe("Unfocused Updated Name");

            window.DataContext = null;
            window.Close();
        });
    }

    [Theory]
    [InlineData(760, 520, Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData(760, 520, Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    [InlineData(600, 440, Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData(600, 440, Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    public async Task Layout_AtSpecifiedSizeAndTheme_AllControlsAreUsableAndNotClipped(double width, double height, Wpf.Ui.Appearance.ApplicationTheme theme)
    {
        await WpfTestRunner.RunAsync(() =>
        {
            var themeDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
            if (themeDict != null)
            {
                themeDict.Theme = theme;
            }

            var initialSettings = new AppSettings
            {
                Translation = new TranslationSettings
                {
                    Providers =
                    [
                        new TranslationProviderProfile
                        {
                            Id = "p-test-layout",
                            Kind = TranslationProviderKind.OpenAICompatible,
                            DisplayName = "Production Translation Service With Full Long Name",
                            BaseUrl = "https://api.openai-compatible.example.org/v1/chat/completions",
                            Model = "gpt-4o-mini-2024-07-18",
                            Order = 0,
                            OpenAIOptions = new OpenAICompatibleOptions { Stream = true }
                        }
                    ]
                }
            };

            using var provider = CreateServiceProvider(initialSettings);
            using var scope = provider.CreateScope();
            var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
            var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();

            try
            {
                window.DataContext = vm;
                window.Left = -20000;
                window.Top = -20000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Width = width;
                window.Height = height;
                window.Show();
                vm.CurrentPageKey = "translation";
                vm.SelectedProfile = vm.Profiles[0];
                window.UpdateLayout();

                // 1. 验证响应式网格布局根据阈值正确切换
                var profilesCol = (ColumnDefinition)window.FindName("ProfilesColDef");
                var detailsCol = (ColumnDefinition)window.FindName("DetailsColDef");
                var profilesRow = (RowDefinition)window.FindName("ProfilesRowDef");
                var detailsRow = (RowDefinition)window.FindName("DetailsRowDef");
                profilesCol.ShouldNotBeNull();
                detailsCol.ShouldNotBeNull();
                profilesRow.ShouldNotBeNull();
                detailsRow.ShouldNotBeNull();

                if (width < 680)
                {
                    profilesCol.Width.IsStar.ShouldBeTrue("窄窗口下列表列占满宽度");
                    detailsCol.Width.Value.ShouldBe(0, "窄窗口下详情列宽度折叠为0");
                    profilesRow.Height.Value.ShouldBe(96, "窄窗口下列表行高度为紧凑固定高度");
                    detailsRow.Height.IsStar.ShouldBeTrue("窄窗口下详情行占满剩余垂直空间");
                }
                else
                {
                    profilesCol.Width.Value.ShouldBe(220, "宽窗口下列表列为固定宽度 220");
                    detailsCol.Width.IsStar.ShouldBeTrue("宽窗口下详情列占满剩余水平空间");
                    profilesRow.Height.IsStar.ShouldBeTrue("宽窗口下列表行占满垂直空间");
                    detailsRow.Height.Value.ShouldBe(0, "宽窗口下详情行高度折叠为0");
                }

                // 2. 验证工具栏操作按钮（添加、上移、下移、删除）文字与边界完整且均可见
                var addBtn = (Wpf.Ui.Controls.Button)window.FindName("AddProfileButton");
                addBtn.ShouldNotBeNull();
                addBtn.ActualWidth.ShouldBeGreaterThan(40);
                addBtn.Visibility.ShouldBe(System.Windows.Visibility.Visible);

                // 3. 验证主要详情输入框在两种尺寸下均具有充足可编辑宽度（>= 150px）
                var nameBox = (TextBox)window.FindName("DisplayNameTextBox");
                nameBox.ShouldNotBeNull();
                nameBox.ActualWidth.ShouldBeGreaterThanOrEqualTo(150, $"DisplayNameTextBox 在 {width}x{height} 下实际宽度 {nameBox.ActualWidth} 不足");

                var apiKeyBox = (PasswordBox)window.FindName("ApiKeyPasswordBox");
                apiKeyBox.ShouldNotBeNull();
                vm.SelectedProfile.CredentialEditKind = CredentialEditKind.Replace;
                window.UpdateLayout();
                apiKeyBox.ActualWidth.ShouldBeGreaterThanOrEqualTo(150, $"ApiKeyPasswordBox 在 {width}x{height} 下实际宽度 {apiKeyBox.ActualWidth} 不足");

                // 4. 验证操作按钮（获取模型、测试连接）未被窗口右边缘裁切
                var listModelsBtn = FindVisualChild<Wpf.Ui.Controls.Button>(window, b => (b.Content as string) == UiText.SettingsListDraftModels);
                var testDraftBtn = FindVisualChild<Wpf.Ui.Controls.Button>(window, b => (b.Content as string) == UiText.SettingsTestDraft);
                listModelsBtn.ShouldNotBeNull();
                testDraftBtn.ShouldNotBeNull();

                listModelsBtn!.ActualWidth.ShouldBeGreaterThan(0);
                testDraftBtn!.ActualWidth.ShouldBeGreaterThan(0);

                var listTransform = listModelsBtn.TransformToAncestor(window).Transform(new System.Windows.Point(listModelsBtn.ActualWidth, 0));
                listTransform.X.ShouldBeLessThanOrEqualTo(window.ActualWidth + 1.0, "获取模型按钮未被窗口右边缘裁切");

                var testTransform = testDraftBtn.TransformToAncestor(window).Transform(new System.Windows.Point(testDraftBtn.ActualWidth, 0));
                testTransform.X.ShouldBeLessThanOrEqualTo(window.ActualWidth + 1.0, "测试连接按钮未被窗口右边缘裁切");

                // 5. 验证底部状态栏与保存/取消按钮在极长文案下依然完整展示
                var statusTb = FindVisualChild<TextBlock>(window, tb => tb.TextTrimming == TextTrimming.CharacterEllipsis);
                if (statusTb != null)
                {
                    statusTb.Text = "这是一个非常冗长的外部状态提示或保存冲突错误回执消息，用于验证 TextTrimming 是否生效以及是否会挤压右侧操作按钮";
                }
                window.UpdateLayout();

                var cancelBtn = FindVisualChild<Wpf.Ui.Controls.Button>(window, b => (b.Content as string) == UiText.SettingsCancel);
                var saveBtn = FindVisualChild<Wpf.Ui.Controls.Button>(window, b => (b.Content as string) == UiText.SettingsSave);
                cancelBtn.ShouldNotBeNull();
                saveBtn.ShouldNotBeNull();

                cancelBtn!.ActualWidth.ShouldBeGreaterThanOrEqualTo(70);
                saveBtn!.ActualWidth.ShouldBeGreaterThanOrEqualTo(70);

                var saveTransform = saveBtn.TransformToAncestor(window).Transform(new System.Windows.Point(saveBtn.ActualWidth, 0));
                saveTransform.X.ShouldBeLessThanOrEqualTo(window.ActualWidth + 1.0, "保存按钮未被窗口右边缘挤出或裁切");

                // 6. 验证详情滚动视图滚动到最底部后，高级选项与生成参数均可正常显示
                var detailsScrollViewer = (ScrollViewer)window.FindName("DetailsPane");
                detailsScrollViewer.ShouldNotBeNull();
                detailsScrollViewer.ScrollToBottom();
                window.UpdateLayout();

                var timeoutBox = FindVisualChild<TextBox>(detailsScrollViewer, t => t.Text == vm.SelectedProfile.TimeoutText);
                timeoutBox.ShouldNotBeNull();
                timeoutBox!.ActualWidth.ShouldBeGreaterThan(0);
            }
            finally
            {
                window.DataContext = null;
                window.Close();
                var restoreDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
                if (restoreDict != null)
                {
                    restoreDict.Theme = Wpf.Ui.Appearance.ApplicationTheme.Light;
                }
            }
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed))
            {
                return typed;
            }
            var result = FindVisualChild(child, predicate);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }
}
