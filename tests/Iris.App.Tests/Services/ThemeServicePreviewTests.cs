using System;
using Iris.App.Services;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Services;

public sealed class ThemeServicePreviewTests
{
    [Fact]
    public void EffectiveTheme_DefaultsToSettingsTheme_WhenNoPreviewActive()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Dark } });

        using var themeService = new ThemeService(settingsService);

        themeService.HasActivePreview.ShouldBeFalse();
        themeService.PreviewOwner.ShouldBeNull();
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public void SetPreview_OverridesPersistentTheme_AndUpdatesEffectiveTheme()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });

        using var themeService = new ThemeService(settingsService);
        var sessionOwner = new object();

        themeService.SetPreview(sessionOwner, AppTheme.Dark);

        themeService.HasActivePreview.ShouldBeTrue();
        themeService.PreviewOwner.ShouldBeSameAs(sessionOwner);
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public void ClearPreview_WithMatchingOwner_RestoresPersistentTheme()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });

        using var themeService = new ThemeService(settingsService);
        var sessionOwner = new object();

        themeService.SetPreview(sessionOwner, AppTheme.Dark);
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

        themeService.ClearPreview(sessionOwner);

        themeService.HasActivePreview.ShouldBeFalse();
        themeService.PreviewOwner.ShouldBeNull();
        themeService.EffectiveTheme.ShouldBe(AppTheme.Light);
    }

    [Fact]
    public void ClearPreview_WithDifferentOwner_DoesNotEndActivePreview()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });

        using var themeService = new ThemeService(settingsService);
        var sessionOwner = new object();
        var alienOwner = new object();

        themeService.SetPreview(sessionOwner, AppTheme.Dark);

        themeService.ClearPreview(alienOwner);

        themeService.HasActivePreview.ShouldBeTrue();
        themeService.PreviewOwner.ShouldBeSameAs(sessionOwner);
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);
    }

    [Fact]
    public void SettingsChanged_WhenPreviewActive_DoesNotOverwriteActivePreview_UntilPreviewCleared()
    {
        var initialSettings = new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } };
        var updatedSettings = new AppSettings { General = new GeneralSettings { Theme = AppTheme.System } };

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(initialSettings);

        using var themeService = new ThemeService(settingsService);
        var sessionOwner = new object();

        themeService.SetPreview(sessionOwner, AppTheme.Dark);
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

        // 模拟外部保存导致 SettingsChanged 事件到达
        settingsService.Current.Returns(updatedSettings);
        settingsService.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settingsService,
            new SettingsChangedEventArgs(initialSettings, updatedSettings));

        // 预览依然优先
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

        // 清除预览后回退到最新持久主题
        themeService.ClearPreview(sessionOwner);
        themeService.EffectiveTheme.ShouldBe(AppTheme.System);
    }

    [Fact]
    public void DisposedService_IgnoresPreviewAndClearOperations()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });

        var themeService = new ThemeService(settingsService);
        var sessionOwner = new object();

        themeService.Dispose();

        themeService.SetPreview(sessionOwner, AppTheme.Dark);
        themeService.HasActivePreview.ShouldBeFalse();

        themeService.ClearPreview(sessionOwner);
        themeService.HasActivePreview.ShouldBeFalse();
    }
}
