using System;
using Iris.App.Services;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Services;

[CollectionDefinition("ThemeServiceDispatcher", DisableParallelization = true)]
public sealed class ThemeServiceDispatcherDefinition;

// Other test classes deliberately occupy Application.Current's dispatcher.
// Observe these real background-to-UI callbacks separately from those fixtures.
[Collection("ThemeServiceDispatcher")]
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

    [Fact]
    public async Task VisualThemeApplier_IsInvoked_WhenThemeChanges()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });

        var appliedTcs = new TaskCompletionSource<Wpf.Ui.Appearance.ApplicationTheme>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var themeService = new ThemeService(
            settingsService,
            logger: null,
            visualThemeApplier: theme => appliedTcs.TrySetResult(theme));

        var sessionOwner = new object();
        themeService.SetPreview(sessionOwner, AppTheme.Dark);

        var lastApplied = await appliedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lastApplied.ShouldBe(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        appliedTcs = new TaskCompletionSource<Wpf.Ui.Appearance.ApplicationTheme>(TaskCreationOptions.RunContinuationsAsynchronously);
        themeService.ClearPreview(sessionOwner);
        var clearedApplied = await appliedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clearedApplied.ShouldBe(Wpf.Ui.Appearance.ApplicationTheme.Light);
    }

    [Fact]
    public async Task VisualThemeApplier_WhenThrows_LogsWarningWithoutCrashing()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });

        var logger = new TestThemeLogger();
        const string privateMarker = "fake-theme-private-marker";
        using var themeService = new ThemeService(
            settingsService,
            logger: logger,
            visualThemeApplier: _ => throw new InvalidOperationException(privateMarker));

        var sessionOwner = new object();
        Should.NotThrow(() => themeService.SetPreview(sessionOwner, AppTheme.Dark));
        themeService.EffectiveTheme.ShouldBe(AppTheme.Dark);

        await logger.LoggedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        logger.Calls.ShouldBe(1);
        logger.LastLevel.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Warning);
        logger.LastException.ShouldBeNull(); // 原生 Exception 必须为 null，不传原始异常对象
        logger.LastFormatted.ShouldContain("Dark");
        logger.LastFormatted.ShouldContain("InvalidOperationException");
        logger.MarkerExposed.ShouldBeFalse();
    }

    private sealed class TestThemeLogger : Microsoft.Extensions.Logging.ILogger<ThemeService>
    {
        public int Calls { get; private set; }
        public Microsoft.Extensions.Logging.LogLevel LastLevel { get; private set; }
        public Exception? LastException { get; private set; }
        public string LastFormatted { get; private set; } = string.Empty;
        public bool MarkerExposed { get; private set; }
        public System.Threading.Tasks.TaskCompletionSource LoggedTcs { get; } = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            LastLevel = logLevel;
            LastException = exception;
            LastFormatted = formatter(state, exception);
            MarkerExposed |= (exception?.ToString() ?? string.Empty).Contains("fake-theme-private-marker", StringComparison.Ordinal)
                || LastFormatted.Contains("fake-theme-private-marker", StringComparison.Ordinal);
            LoggedTcs.TrySetResult();
        }
    }
}
