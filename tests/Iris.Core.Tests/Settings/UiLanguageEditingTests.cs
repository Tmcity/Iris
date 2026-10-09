using System.Globalization;
using Iris.Core.DependencyInjection;
using Iris.Core.Errors;
using Iris.Core.Localization;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Settings;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationGroup;

[Collection("Localization")]
public sealed class UiLanguageEditingTests
{
    [Theory][InlineData("en-US")][InlineData("zh-CN")]
    public async Task LanguageOnlyPlanCommitsOnceWithoutProviderApplyOrSecrets(string target)
    {
        using var f = new Fixture();
        f.Store.Value = f.Store.Value with { General = f.Store.Value.General with { UiLanguage = target == "en-US" ? "zh-CN" : "en-US" } };
        string baseline = f.Store.Value.General.UiLanguage;
        var result = await f.CommitAsync(new(new() { UiLanguage = new(baseline, target) }, []));
        result.Status.ShouldBe(SettingsEditStatus.Applied); result.PendingApply.ShouldBeNull();
        f.Store.Writes.ShouldBe(1); f.Store.Current.General.UiLanguage.ShouldBe(target);
        f.Secrets.ReceivedCalls().ShouldBeEmpty();
        f.Translation.ReceivedCalls().ShouldBeEmpty(); f.Ocr.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory][InlineData("en-us")][InlineData("EN-US")][InlineData("fr-FR")][InlineData("")]
    public async Task ExplicitNoncanonicalLanguageIsRejectedWithoutWriting(string target)
    {
        using var f = new Fixture();
        var result = await f.CommitAsync(new(new() { UiLanguage = new("zh-CN", target) }, []));
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Store.Writes.ShouldBe(0); f.Secrets.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ConflictAndAlreadyAppliedTargetFollowTheOriginalFieldProtocol()
    {
        using var f = new Fixture();
        var conflict = await f.CommitAsync(new(new() { UiLanguage = new("legacy-baseline", "en-US") }, []));
        conflict.IsConflict.ShouldBeTrue(); f.Store.Writes.ShouldBe(0);
        f.Store.Value = f.Store.Value with { General = f.Store.Value.General with { UiLanguage = "en-US", LogLevel = "Warning" } };
        var merged = await f.CommitAsync(new(new() { UiLanguage = new("zh-CN", "en-US") }, []));
        merged.Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Current.General.UiLanguage.ShouldBe("en-US");
        f.Store.Current.General.LogLevel.ShouldBe("Warning");
        await f.CommitAsync(new(new(Theme: new(AppTheme.System, AppTheme.Dark)), []));
        f.Store.Current.General.UiLanguage.ShouldBe("en-US"); f.Store.Current.General.Theme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public async Task EmptyPatchAndReadonlyRemainSideEffectFree_AndFourArgumentDeconstructionStillWorks()
    {
        using var f = new Fixture();
        var original = new GeneralSettingsPatch(null, null, null, null) { UiLanguage = new("zh-CN", "en-US") };
        var (theme, launch, notification, log) = original;
        theme.ShouldBeNull(); launch.ShouldBeNull(); notification.ShouldBeNull(); log.ShouldBeNull();
        (await f.CommitAsync(new(new(), []))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Store.Writes.ShouldBe(0);
        f.Store.ReadOnly = true;
        (await f.CommitAsync(new(original, []))).Status.ShouldBe(SettingsEditStatus.Uncommitted);
        f.Store.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ExplicitRunLanguageSurvivesDifferentThreadCulturesAndLaterSelections()
    {
        using var run = UiLanguage.BeginRun();
        UiLanguage.Select("en-US");
        string text = ErrorMessages.GetDefault(ErrorCode.HotkeyConflict);
        text.ShouldBe("This shortcut is in use by another program");
        string background = await Task.Run(() =>
        {
            CultureInfo.CurrentCulture = new("fr-FR"); CultureInfo.CurrentUICulture = new("zh-CN");
            return ErrorMessages.GetDefault(ErrorCode.HotkeyConflict);
        });
        background.ShouldBe(text); UiLanguage.Select("zh-CN"); UiLanguage.Name.ShouldBe("en-US");
        ErrorMessages.GetDefault((ErrorCode)int.MaxValue).ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Store Store = new();
        internal readonly ISecretStore Secrets = Substitute.For<ISecretStore>();
        internal readonly ITranslationProviderRegistry Translation = Substitute.For<ITranslationProviderRegistry>();
        internal readonly IOcrProviderRegistry Ocr = Substitute.For<IOcrProviderRegistry>();
        internal readonly ServiceProvider Services;
        internal ISettingsEditCoordinator Editor => Services.GetRequiredService<ISettingsEditCoordinator>();
        internal Task<SettingsEditResult> CommitAsync(SettingsEditPlan plan) => Editor.CommitAsync(plan, TestContext.Current.CancellationToken);
        internal Fixture()
        {
            var services = new ServiceCollection(); services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>)); services.AddIrisCore();
            services.AddSingleton<ISettingsService>(Store); services.AddSingleton(Secrets); services.AddSingleton(Translation); services.AddSingleton(Ocr);
            Services = services.BuildServiceProvider();
        }
        public void Dispose() => Services.Dispose();
    }
    private sealed class Store : ISettingsService
    {
        internal AppSettings Value = new(); internal int Writes; internal bool ReadOnly;
        public AppSettings Current => Value;
        public bool IsReadOnly => ReadOnly;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Writes++;
            var old = Value; Value = mutate(old); Changed?.Invoke(this, new(old, Value)); return Task.CompletedTask;
        }
    }
}
