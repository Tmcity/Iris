using System.Globalization;
using System.Collections.Concurrent;
using System.Resources;
using System.Text.Json;
using System.Windows.Controls;
using Iris.App.Hosting;
using Iris.App.Resources;
using Iris.App.Settings;
using Iris.App.Tests.Settings;
using Iris.App.Tests.TestSupport;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Platform;
using Iris.Core.Security;
using Iris.Core.Serialization;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Serilog.Core;
using Shouldly;

namespace Iris.App.Tests.Localization;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationGroup;

[Collection("Localization")]
public sealed class LocalizationTests
{
    [Fact]
    public async Task RealWindowLanguageOnlySavePersistsOnceAndReopenedWindowStillUsesCurrentLanguage()
    {
        using var language = AppLanguage.BeginRun(); AppLanguage.Select("zh-CN");
        byte[]? saved = null;
        await NetworkSettingsUiScope.Run(async f =>
        {
            f.Show(); f.Vm.CurrentPageKey = "general";
            var combo = (ComboBox)f.Window.FindName("UiLanguageCombo");
            combo.SelectedValue = "en-US";
            f.Vm.InterfaceLanguage.Value.ShouldBe("en-US"); f.Vm.IsDirty.ShouldBeTrue();
            await f.Vm.ExecuteSaveAsync();
            f.Updates.ShouldBe(1); f.Secrets.Writes.ShouldBe(0); f.Receipts.PendingCount.ShouldBe(0);
            f.Settings.Current.General.UiLanguage.ShouldBe("en-US"); f.Vm.IsDirty.ShouldBeFalse();
            AppLanguage.Name.ShouldBe("zh-CN"); f.Vm.InterfaceLanguageHint.ShouldBe(UiText.LanguageRestartPending);
            saved = await File.ReadAllBytesAsync(f.SettingsFile);
            await f.Session.ShutdownAsync();
            var presenter = f.Services.GetRequiredService<SettingsWindowPresenter>();
            await presenter.ShowAsync("general");
            presenter.ActiveSession!.ViewModel.InterfaceLanguage.Value.ShouldBe("en-US");
            presenter.ActiveSession.Window.Title.ShouldBe(UiText.SettingsWindowTitle);
            presenter.ActiveSession.ViewModel.InterfaceLanguageHint.ShouldBe(UiText.LanguageRestartPending);
            await presenter.ShutdownAsync();
        });
        using var nextRun = AppLanguage.BeginRun();
        await LanguageHost.Run(saved!, async f =>
        {
            await f.Bootstrap.StartAsync();
            AppLanguage.Name.ShouldBe("en-US"); UiText.SettingsTitle.ShouldBe("Settings");
        });
    }

    [Fact]
    public Task MixedPlanPendingApplyAndRetryDoNotSwitchOrResaveNewLanguage() => InChinese(() => NetworkSettingsUiScope.Run(async f =>
    {
        f.Show(); f.Vm.InterfaceLanguage.Value = "en-US"; f.Vm.Network.Mode.Value = ProxyMode.System;
        f.Vm.History.MaxEntriesText.Value = "321"; f.NetworkObserver.Fail = true;
        await f.Vm.ExecuteSaveAsync();
        f.Updates.ShouldBe(1); f.Settings.Current.General.UiLanguage.ShouldBe("en-US");
        f.Settings.Current.History.MaxEntries.ShouldBe(321); f.Receipts.PendingCount.ShouldBe(1);
        AppLanguage.Name.ShouldBe("zh-CN");
        f.Vm.InterfaceLanguage.Value = "zh-CN"; f.NetworkObserver.Fail = false;
        await f.Vm.ExecuteRetryPendingApplyAsync();
        f.Updates.ShouldBe(1); f.Settings.Current.General.UiLanguage.ShouldBe("en-US");
        f.Vm.InterfaceLanguage.Value.ShouldBe("zh-CN"); f.Vm.InterfaceLanguage.IsDirty.ShouldBeTrue();
        AppLanguage.Name.ShouldBe("zh-CN");
    }));

    [Fact]
    public Task ExternalMergeConflictReloadAndCancelPreserveTheOriginalJointEditRules() => InChinese(() => NetworkSettingsUiScope.Run(async f =>
    {
        f.Show(); f.Vm.CurrentPageKey = "general"; await f.IdleAsync();
        await ExternalUpdateAsync(f, s => s with { General = s.General with { UiLanguage = "en-US" } }, async () =>
        {
            await f.IdleAsync();
            Console.WriteLine($"R1_BEFORE_RELEASE persisted={f.Settings.Current.General.UiLanguage} draft={f.Vm.InterfaceLanguage.Value} dirty={f.Vm.InterfaceLanguage.IsDirty}");
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(f.SettingsFile));
            json.RootElement.GetProperty("general").GetProperty("uiLanguage").GetString().ShouldBe("en-US");
            f.Vm.InterfaceLanguage.Value.ShouldBe("zh-CN"); f.Vm.InterfaceLanguage.IsDirty.ShouldBeFalse();
        });
        f.Vm.InterfaceLanguage.Value.ShouldBe("en-US"); f.Vm.InterfaceLanguage.IsDirty.ShouldBeFalse();
        var baselineTheme = f.Settings.Current.General.Theme;
        var targetTheme = Enum.GetValues<AppTheme>().First(theme => theme != baselineTheme);
        var externalTheme = Enum.GetValues<AppTheme>().Single(theme => theme != baselineTheme && theme != targetTheme);
        f.Vm.InterfaceLanguage.Value = "zh-CN"; f.Vm.Theme = targetTheme;
        await ExternalUpdateAsync(f, s => s with { General = s.General with { Theme = externalTheme } });
        await f.Vm.ExecuteSaveAsync(); f.Vm.IsConflict.ShouldBeTrue();
        f.Settings.Current.General.UiLanguage.ShouldBe("en-US"); f.Vm.InterfaceLanguage.Value.ShouldBe("zh-CN");
        f.Vm.ExecuteReload(); f.Vm.InterfaceLanguage.Value.ShouldBe("en-US"); f.Vm.IsDirty.ShouldBeFalse();
        f.Vm.InterfaceLanguage.Value = "zh-CN"; f.Vm.ExecuteCancel();
        f.Settings.Current.General.UiLanguage.ShouldBe("en-US"); AppLanguage.Name.ShouldBe("zh-CN");
    }));

    private static async Task ExternalUpdateAsync(NetworkSettingsUiScope f, Func<AppSettings, AppSettings> update,
        Func<Task>? beforeMerge = null)
    {
        var gate = f.NewGate();
        var work = new ConcurrentQueue<Task>();
        var merged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? primary = null;
        var cleanup = new List<Exception>();
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.InterfaceLanguageHint)) merged.TrySetResult();
        };
        void Track(Task task) => work.Enqueue(task);
        f.Vm.PropertyChanged += changed;
        f.Ui.InvocationStarted += Track;
        f.Ui.PauseWhen(() => true, gate, after: false);
        try
        {
            await f.Settings.UpdateAsync(update);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            merged.Task.IsCompleted.ShouldBeFalse();
            if (beforeMerge != null) await beforeMerge();
            gate.Open();
            await merged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            gate.Open(); f.Ui.Clear();
            try
            {
                // Join actual dispatcher invocations, including callbacks they enqueue while being drained.
                while (work.TryDequeue(out var task))
                {
                    _ = f.Track(task); // The enclosing fixture also joins the original task if the bounded wait fails.
                    try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (Exception ex) { cleanup.Add(ex); }
                }
                Console.WriteLine($"R1_JOINED persisted={f.Settings.Current.General.UiLanguage} draft={f.Vm.InterfaceLanguage.Value} dirty={f.Vm.InterfaceLanguage.IsDirty}");
            }
            finally { f.Ui.InvocationStarted -= Track; f.Vm.PropertyChanged -= changed; }
        }
        if (primary != null && cleanup.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }

    [Fact]
    public Task FutureReadonlyDisablesTheActualLanguageSelector() => InChinese(() => NetworkSettingsUiScope.Run(async f =>
    {
        f.Show(); f.Vm.CurrentPageKey = "general"; await f.IdleAsync();
        f.Settings.IsReadOnly.ShouldBeTrue(); f.Vm.CanEdit.ShouldBeFalse();
        ((ComboBox)f.Window.FindName("UiLanguageCombo")).IsEnabled.ShouldBeFalse();
        f.Vm.InterfaceLanguage.Value = "en-US"; f.Vm.InterfaceLanguage.Value.ShouldBe("zh-CN");
        await f.Vm.ExecuteSaveAsync(); f.Updates.ShouldBe(0);
    }, readOnly: true));

    [Theory][InlineData("en-US", "en-US", false)][InlineData("EN-us", "en-US", false)]
    [InlineData("unsupported", "zh-CN", false)][InlineData("en-US", "en-US", true)]
    public async Task ActualStartupSelectsNormalizedLanguageAfterLoadAndEarlyObjectsUseIt(string saved, string expected, bool future)
    {
        using var run = AppLanguage.BeginRun();
        var settings = new AppSettings { General = new() { UiLanguage = saved }, Network = new() { ProxyMode = ProxyMode.None }, SchemaVersion = future ? 999 : AppSettings.CurrentSchemaVersion };
        await LanguageHost.Run(JsonSerializer.SerializeToUtf8Bytes(settings, IrisJson.Options), async f =>
        {
            var early = f.Host.Services.GetRequiredService<HotkeyCoordinator>();
            _ = early.GetRuntimeSnapshot(); _ = OcrSettingsDraftViewModel.Layouts;
            _ = UiText.HistoryMetadataFormat; _ = new Iris.App.History.HistoryRow(new(1, DateTimeOffset.UtcNow, TextOrigin.Input, "x", "en", "zh-Hans", false, 1, false));
            await f.Bootstrap.StartAsync(); AppLanguage.Name.ShouldBe(expected);
            var errors = expected == "en-US" ? "Invalid argument" : "参数无效";
            Task<string> background;
            using (ExecutionContext.SuppressFlow()) background = Task.Run(() =>
            {
                CultureInfo.CurrentCulture = new("fr-FR"); CultureInfo.CurrentUICulture = new("tr-TR");
                return ErrorMessages.GetDefault(ErrorCode.InvalidArgument);
            });
            (await background).ShouldBe(errors);
            if (expected == "en-US")
            {
                UiText.SettingsTitle.ShouldBe("Settings"); OcrSettingsDraftViewModel.Layouts[0].Label.ShouldBe("Keep original line breaks");
                HotkeyCoordinator.FormatKeyCombo(null).ShouldBe("(Not set)");
            }
            AppLanguage.Select(expected == "en-US" ? "zh-CN" : "en-US"); AppLanguage.Name.ShouldBe(expected);
            f.Host.Services.GetRequiredService<ISettingsService>().IsReadOnly.ShouldBe(future);
        });
    }

    [Fact]
    public async Task CorruptRecoveryChoosesDefaultLanguageBeforeCreatingItsNotice()
    {
        using var run = AppLanguage.BeginRun();
        await LanguageHost.Run("broken synthetic json"u8.ToArray(), async f =>
        {
            await f.Bootstrap.StartAsync(); AppLanguage.Name.ShouldBe("zh-CN");
            f.FatalMessages.ShouldBeEmpty();
            var service = f.Host.Services.GetRequiredService<Iris.App.Services.TrayNotificationService>();
            var pending = (System.Collections.IEnumerable)typeof(Iris.App.Services.TrayNotificationService)
                .GetField("_pendingNotifications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            pending.Cast<object>().Select(item => (string)item.GetType().GetProperty("Message")!.GetValue(item)!)
                .ShouldContain(UiText.SettingsCorruptedNotice);
        });
    }

    [Theory][InlineData("en-GB", "Iris command line usage:")][InlineData("fr-FR", "Iris 命令行用法：")]
    public void StartupHelpUsesSupportedSystemFallbackWithoutReadingConfiguration(string culture, string prefix)
    {
        using var run = AppLanguage.BeginRun(); var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new(culture);
            var runtime = new HelpRuntime(); Program.Run(["--help"], runtime).ShouldBe(0);
            runtime.Messages.Single().ShouldStartWith(prefix); runtime.ResourceCalls.ShouldBe(0);
            Program.Run(["--action", "12"], runtime).ShouldBe(2); runtime.ResourceCalls.ShouldBe(0);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void MissingResourceFormatFailureAndFatalFeedbackNeverExposeKeysOrRawErrors()
    {
        using var run = AppLanguage.BeginRun(); AppLanguage.Select("en-US");
        AppResources.Get("MissingSyntheticKey").ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
        AppResources.Format("HistoryPageFormat", 1).ShouldBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
        FatalErrorFormatter.Format(new InvalidOperationException("private secret")).ShouldNotContain("private secret");
        FatalErrorFormatter.Format(new IrisException(ErrorCode.StorageFailure, "private secret", detail: "private details"))
            .ShouldBe(ErrorMessages.GetDefault(ErrorCode.StorageFailure));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ActualPresetMenuLocalizesLabelsByStableKeyWithoutChangingPresetOrSavedNames(bool small)
    {
        string before = JsonSerializer.Serialize(TranslationProviderPresets.All, IrisJson.Options);
        using var run = AppLanguage.BeginRun(); AppLanguage.Select("en-US");
        await NetworkSettingsUiScope.Run(async f =>
        {
            f.Window.Width = small ? 600 : 760; f.Window.Height = small ? 440 : 520;
            f.Show(); f.Vm.CurrentPageKey = "translation"; await f.IdleAsync();
            string savedName = f.Settings.Current.Translation.Providers[0].DisplayName;
            var button = (Button)f.Window.FindName("AddProfileButton");
            button.IsVisible.ShouldBeTrue(); button.IsEnabled.ShouldBeTrue(); button.ActualWidth.ShouldBeGreaterThan(70);
            var center = button.TranslatePoint(new System.Windows.Point(button.ActualWidth / 2, button.ActualHeight / 2), f.Window);
            var hit = f.Window.InputHitTest(center) as System.Windows.DependencyObject;
            while (hit != null && !ReferenceEquals(hit, button)) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            hit.ShouldBeSameAs(button);
            button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            var menu = button.ContextMenu!;
            try
            {
                var items = menu.Items.Cast<MenuItem>().ToDictionary(item => ((TranslationProviderPreset)item.Tag).Key);
                items["custom"].Header.ShouldBe("Custom OpenAI-compatible provider");
                items["ollama"].Header.ShouldBe("Ollama (local)");
                items["gemini"].Header.ShouldBe("Gemini (OpenAI-compatible endpoint)");
                items["dashscope"].Header.ShouldBe("Qwen (DashScope)");
                f.Settings.Current.Translation.Providers[0].DisplayName.ShouldBe(savedName);
                f.Updates.ShouldBe(0);
            }
            finally { menu.IsOpen = false; }
        });
        JsonSerializer.Serialize(TranslationProviderPresets.All, IrisJson.Options).ShouldBe(before);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task EnglishShortcutSafetyDescriptionHasItsFullWrappedHeight(bool small)
    {
        using var run = AppLanguage.BeginRun(); AppLanguage.Select("en-US");
        await NetworkSettingsUiScope.Run(async f =>
        {
            f.Window.Width = small ? 600 : 760; f.Window.Height = small ? 440 : 520;
            f.Show(); f.Vm.CurrentPageKey = "hotkeys"; await f.IdleAsync();
            var text = FindText(f.Window, UiText.SelectionHotkeyDescription)!; text.ShouldNotBeNull();
            text.BringIntoView(); await f.IdleAsync();
            var expected = new System.Windows.Media.FormattedText(text.Text, CultureInfo.GetCultureInfo(AppLanguage.Name), System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
                System.Windows.Media.VisualTreeHelper.GetDpi(text).PixelsPerDip) { MaxTextWidth = text.ActualWidth };
            text.ActualHeight.ShouldBeGreaterThanOrEqualTo(expected.Height - 1);
        });
    }
    private static TextBlock? FindText(System.Windows.DependencyObject root, string text)
    {
        if (root is TextBlock value && value.Text == text) return value;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(System.Windows.Media.VisualTreeHelper.GetChild(root, i), text) is { } found) return found;
        return null;
    }

    private static async Task InChinese(Func<Task> body)
    { using var run = AppLanguage.BeginRun(); AppLanguage.Select("zh-CN"); await body(); }

    private sealed class HelpRuntime : StartupRuntime
    {
        internal readonly List<string> Messages = []; internal int ResourceCalls;
        private InvalidOperationException Unexpected() { ResourceCalls++; return new InvalidOperationException("Startup must have no side effects"); }
        internal override IpcIdentity GetIdentity() => throw Unexpected();
        internal override IDisposable? Acquire(IpcIdentity identity) => throw Unexpected();
        internal override Task<IpcSendResult> ForwardAsync(IpcIdentity identity, IpcCommand command) => throw Unexpected();
        internal override IAppPaths CreatePaths() => throw Unexpected();
        internal override void OpenLogger(IAppPaths paths, LoggingLevelSwitch levelSwitch) => throw Unexpected();
        internal override IHost BuildHost(IpcIdentity identity, IpcCommand? command, IAppPaths paths, LoggingLevelSwitch levelSwitch, long startTimestamp) => throw Unexpected();
        internal override int RunApplication(IHost host) => throw Unexpected();
        internal override void CloseLogger() => throw Unexpected();
        internal override void LogFailure(string exceptionType) => throw Unexpected();
        internal override void Show(string message) => Messages.Add(message);
    }
}

internal sealed class LanguageHost : IAsyncDisposable
{
    private readonly TempDirectory _directory = new();
    internal IHost Host = null!;
    internal AppBootstrapper Bootstrap => Host.Services.GetRequiredService<AppBootstrapper>();
    internal readonly List<string> FatalMessages = [];
    internal static Task Run(byte[] json, Func<LanguageHost, Task> body) => WpfTestRunner.RunAsync(async () =>
    {
        var f = new LanguageHost(); var oldExit = AppBootstrapper.AppShutdowner; var oldFatal = AppBootstrapper.FatalErrorShower;
        AppBootstrapper.AppShutdowner = _ => { }; AppBootstrapper.FatalErrorShower = (_, message) => f.FatalMessages.Add(message);
        Exception? primary = null; var errors = new List<Exception>();
        try { await f.Initialize(json); await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            try { await f.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            AppBootstrapper.AppShutdowner = oldExit; AppBootstrapper.FatalErrorShower = oldFatal;
        }
        if (primary != null && errors.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    });
    private async Task Initialize(byte[] json)
    {
        var paths = new FakeAppPaths(_directory.Path); await File.WriteAllBytesAsync(paths.SettingsFile, json);
        var hotkeys = Substitute.For<IHotkeyService>();
        hotkeys.Register(Arg.Any<IrisAction>(), Arg.Any<HotkeyBinding>()).Returns(call => new HotkeyRegistrationResult(call.Arg<IrisAction>(), true));
        Host = IsolatedAppHost.Build([], paths, new(), 0, services =>
        {
            services.AddSingleton(Substitute.For<ISecretStore>()); services.AddSingleton(hotkeys);
            services.AddSingleton(Substitute.For<IHotkeyCaptureService>()); services.AddSingleton(Substitute.For<IAutoStartService>());
            services.AddSingleton(Substitute.For<ITranslationProviderRegistry>()); services.AddSingleton(Substitute.For<IOcrProviderRegistry>());
        });
        Host.Services.GetRequiredService<TrayIconController>().SuppressIconCreationForTesting = true;
    }
    public async ValueTask DisposeAsync()
    {
        try { if (Host != null) { await Bootstrap.StopAsync(); await ((IAsyncDisposable)Host).DisposeAsync(); } }
        finally { _directory.Dispose(); }
    }
}
