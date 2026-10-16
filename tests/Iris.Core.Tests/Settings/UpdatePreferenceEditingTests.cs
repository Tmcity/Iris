using Iris.Core.DependencyInjection;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Settings;

public sealed class UpdatePreferenceEditingTests
{
    [Fact]
    public async Task PreferenceOnlyPlanPersistsWithoutApplyAndNullPreservesLatestPreference()
    {
        var settings = Substitute.For<ISettingsService>();
        var current = new AppSettings(); int writes = 0;
        settings.Current.Returns(_ => current);
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(call => { current = call.Arg<Func<AppSettings, AppSettings>>()(current); writes++; return Task.CompletedTask; });
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddIrisCore();
        services.AddSingleton(settings); services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
        await using var provider = services.BuildServiceProvider();
        var editor = provider.GetRequiredService<ISettingsEditCoordinator>();
        var patch = new GeneralSettingsPatch(null, null, null, null) { CheckForUpdates = new(true, false) };
        var (theme, launch, notification, log) = patch;
        theme.ShouldBeNull(); launch.ShouldBeNull(); notification.ShouldBeNull(); log.ShouldBeNull();
        var result = await editor.CommitAsync(new(patch, []), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Applied); result.PendingApply.ShouldBeNull(); writes.ShouldBe(1);
        current.General.CheckForUpdates.ShouldBeFalse();
        await editor.CommitAsync(new(new() { UiLanguage = new("zh-CN", "en-US") }, []), TestContext.Current.CancellationToken);
        current.General.CheckForUpdates.ShouldBeFalse(); current.General.UiLanguage.ShouldBe("en-US");
        settings.IsReadOnly.Returns(true);
        (await editor.CommitAsync(new(patch with { CheckForUpdates = new(false, true) }, []), TestContext.Current.CancellationToken))
            .Status.ShouldBe(SettingsEditStatus.Uncommitted);
        writes.ShouldBe(2);
    }
}
