using System.Reflection;
using Iris.App.Hosting;
using Iris.App.Resources;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

// Drill-down basics: the two view-only flags, the reset on page change (T5), four new strings.
// Lives in the non-parallel "Localization" collection: the last test switches the process-wide UI language.
[Collection("Localization")]
public sealed class SettingsViewModelDetailViewTests
{
    // Copied from SettingsWindowTranslationUiTests.CreateServiceProvider.
    private static ServiceProvider CreateServiceProvider()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

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

    // View model only, no window.
    private static Task WithViewModel(Action<SettingsViewModel> body) => WpfTestRunner.RunAsync(() =>
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();
        body(scope.ServiceProvider.GetRequiredService<SettingsViewModel>());
        return Task.CompletedTask;
    });

    // The window is never shown: setting DataContext is enough to subscribe to the view model.
    private static Task WithWindow(Action<SettingsWindow, SettingsViewModel> body) => WpfTestRunner.RunAsync(() =>
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
        window.DataContext = vm;
        body(window, vm);
        return Task.CompletedTask;
    });

    private static void Set(SettingsViewModel vm, string property, bool value)
    {
        if (property == nameof(SettingsViewModel.IsTranslationDetailOpen)) vm.IsTranslationDetailOpen = value;
        else vm.IsOcrDetailOpen = value;
    }

    private static bool Get(SettingsViewModel vm, string property) =>
        property == nameof(SettingsViewModel.IsTranslationDetailOpen) ? vm.IsTranslationDetailOpen : vm.IsOcrDetailOpen;

    [Fact]
    public Task DetailFlags_DefaultToFalse() => WithViewModel(vm =>
    {
        vm.IsTranslationDetailOpen.ShouldBeFalse();
        vm.IsOcrDetailOpen.ShouldBeFalse();
    });

    [Theory]
    [InlineData(nameof(SettingsViewModel.IsTranslationDetailOpen))]
    [InlineData(nameof(SettingsViewModel.IsOcrDetailOpen))]
    public Task DetailFlag_NotifiesOnceWhenTheValueChanges_AndNotWhenItIsTheSame(string property) => WithViewModel(vm =>
    {
        var raised = new List<string?>();
        vm.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

        Set(vm, property, true);
        Get(vm, property).ShouldBeTrue();
        raised.Count.ShouldBe(1);
        raised[0].ShouldBe(property);

        Set(vm, property, true); // same value: no notification
        raised.Count.ShouldBe(1);

        Set(vm, property, false);
        Get(vm, property).ShouldBeFalse();
        raised.Count.ShouldBe(2);
        raised[1].ShouldBe(property);

        Set(vm, property, false); // same value: no notification
        raised.Count.ShouldBe(2);
    });

    [Fact]
    public Task DetailFlags_AreIndependent_AndNeverTouchTheDraft() => WithViewModel(vm =>
    {
        bool dirtyBefore = vm.IsDirty;
        var raised = new List<string?>();
        vm.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

        vm.IsTranslationDetailOpen = true;
        vm.IsOcrDetailOpen.ShouldBeFalse();

        vm.IsOcrDetailOpen = true;
        vm.IsTranslationDetailOpen.ShouldBeTrue();

        vm.IsTranslationDetailOpen = false;
        vm.IsOcrDetailOpen.ShouldBeTrue();

        // View-only state: it does not count as a change and raises nothing but its own notification.
        vm.IsDirty.ShouldBe(dirtyBefore);
        raised.Count.ShouldBe(3);
        raised[0].ShouldBe(nameof(SettingsViewModel.IsTranslationDetailOpen));
        raised[1].ShouldBe(nameof(SettingsViewModel.IsOcrDetailOpen));
        raised[2].ShouldBe(nameof(SettingsViewModel.IsTranslationDetailOpen));
    });

    private static void SetDetailId(SettingsWindow window, string field, string? value) =>
        typeof(SettingsWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    // This task has no "enter detail" yet (T1 belongs to tasks 11 and 12), so both pages are put into the detail state directly.
    private static void PutBothPagesIntoDetail(SettingsWindow window, SettingsViewModel vm)
    {
        vm.IsTranslationDetailOpen = true;
        vm.IsOcrDetailOpen = true;
        SetDetailId(window, "_translationDetailId", "translation-item");
        SetDetailId(window, "_ocrDetailId", "ocr-item");
        window.TranslationDetailId.ShouldBe("translation-item");
        window.OcrDetailId.ShouldBe("ocr-item");
    }

    // T5: the page key changed, so both pages go back to their list and both ids are cleared.
    [Theory]
    [InlineData("translation", "general")]
    [InlineData("translation", "ocr")]
    [InlineData("ocr", "translation")]
    [InlineData("ocr", "history")]
    public Task PageChange_ClosesBothDetailViews_AndClearsBothIds(string from, string to) => WithWindow((window, vm) =>
    {
        vm.CurrentPageKey = from;
        PutBothPagesIntoDetail(window, vm);

        vm.CurrentPageKey = to;

        vm.CurrentPageKey.ShouldBe(to);
        vm.IsTranslationDetailOpen.ShouldBeFalse();
        vm.IsOcrDetailOpen.ShouldBeFalse();
        window.TranslationDetailId.ShouldBeNull();
        window.OcrDetailId.ShouldBeNull();
    });

    // Writing a page key that normalizes to the current one raises no notification, so the detail state
    // stays (plan 2.13 item 11; S0-8: showing the same page again keeps the detail view).
    [Theory]
    [InlineData("translation", "translation")]
    [InlineData("translation", " Translation ")]
    [InlineData("ocr", "OCR")]
    [InlineData("general", "about")] // a page that is not open yet falls back to "general"
    public Task SamePageKey_KeepsTheDetailFlags(string current, string written) => WithWindow((window, vm) =>
    {
        vm.CurrentPageKey = current;
        PutBothPagesIntoDetail(window, vm);
        var raised = new List<string?>();
        vm.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

        vm.CurrentPageKey = written;

        vm.CurrentPageKey.ShouldBe(current);
        raised.ShouldBeEmpty();
        vm.IsTranslationDetailOpen.ShouldBeTrue();
        vm.IsOcrDetailOpen.ShouldBeTrue();
        window.TranslationDetailId.ShouldBe("translation-item");
        window.OcrDetailId.ShouldBe("ocr-item");
    });

    private static string ReadText(string key) => key switch
    {
        nameof(UiText.SettingsRowMoreActions) => UiText.SettingsRowMoreActions,
        nameof(UiText.SettingsRowOpenDetails) => UiText.SettingsRowOpenDetails,
        nameof(UiText.SettingsRowDragToReorder) => UiText.SettingsRowDragToReorder,
        nameof(UiText.SettingsRowEnabled) => UiText.SettingsRowEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    // A missing key does not throw: AppResources.Get returns the "unknown error" sentence, so both languages
    // are checked value by value. Reading one property in Chinese and then in English also proves it is a
    // => property and not a cached value.
    [Theory]
    [InlineData("SettingsRowMoreActions", "更多操作", "More actions")]
    [InlineData("SettingsRowOpenDetails", "查看详情", "Open details")]
    [InlineData("SettingsRowDragToReorder", "拖动排序", "Drag to reorder")]
    [InlineData("SettingsRowEnabled", "启用", "Enabled")]
    public void NewRowText_ResolvesInChineseAndEnglish(string key, string chinese, string english)
    {
        using (AppLanguage.BeginRun())
        {
            AppLanguage.Select("zh-CN");
            ReadText(key).ShouldNotBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            ReadText(key).ShouldBe(chinese);
            AppResources.Get(key).ShouldBe(chinese); // the property name is also the resource key
        }

        using (AppLanguage.BeginRun())
        {
            AppLanguage.Select("en-US");
            ReadText(key).ShouldNotBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            ReadText(key).ShouldBe(english);
            AppResources.Get(key).ShouldBe(english);
        }
    }
}
