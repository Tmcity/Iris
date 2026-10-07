using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tray;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Iris.Core.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

[Collection("AppShutdownerSharedState")]
public sealed class OcrSettingsLifecycleTests
{
    [Theory][InlineData("save")][InlineData("reload")][InlineData("close")]
    public Task RetryUiPublication_IsIncludedInEveryDrainBarrier(string operation) => Run(async f =>
    {
        f.Show(); f.Cloud.Configure = p => p.FailDispose = true;
        await f.Vm.ExecuteOcrTestAsync(); f.Vm.HasFailedOcrCleanup.ShouldBeTrue();
        var provider = f.Cloud.Created.Single(); provider.FailDispose = false;
        f.Vm.Ocr.AutoCopyOnOcr.Value = true;
        var gate = f.NewGate(); f.Ui.PauseNext(gate);
        var retry = f.Track(f.Vm.RetryOcrCleanupAsync());
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Disposed.ShouldBeTrue(); retry.IsCompleted.ShouldBeFalse();
        await f.Vm.ExecuteOcrTestAsync(); f.Cloud.Created.Count.ShouldBe(1);
        Task? drain = null;
        if (operation == "save") drain = f.Track(f.Vm.ExecuteSaveAsync());
        else if (operation == "close") drain = f.Track(f.Session.ShutdownAsync());
        else f.Vm.ExecuteReload();
        await f.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        f.Settings.Updates.ShouldBe(0, "No commit is allowed before the original retry finishes its UI publication.");
        if (drain != null) drain.IsCompleted.ShouldBeFalse();
        else { f.Vm.IsSaving.ShouldBeTrue(); f.Vm.Ocr.AutoCopyOnOcr.Value.ShouldBeTrue(); }
        gate.Open(); await retry;
        if (drain != null) await drain;
        else
        {
            for (int i = 0; i < 100 && f.Vm.IsSaving; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
            f.Vm.IsSaving.ShouldBeFalse(); f.Vm.IsDirty.ShouldBeFalse();
        }
        f.Settings.Updates.ShouldBe(operation == "save" ? 1 : 0);
        provider.Tests.ShouldBe(1); provider.Disposals.ShouldBe(2);
    });

    [Fact]
    public Task FailedCleanupRetry_KeepsSameSessionForAnotherAttempt() => Run(async f =>
    {
        f.Cloud.Configure = p => p.FailDispose = true;
        await f.Vm.ExecuteOcrTestAsync(); var provider = f.Cloud.Created.Single();
        await f.Vm.RetryOcrCleanupAsync(); f.Vm.HasFailedOcrCleanup.ShouldBeTrue(); provider.Disposed.ShouldBeFalse();
        provider.Tests.ShouldBe(1); provider.Disposals.ShouldBe(2);
        provider.FailDispose = false; await f.Vm.RetryOcrCleanupAsync();
        provider.Tests.ShouldBe(1); provider.Disposals.ShouldBe(3); provider.Disposed.ShouldBeTrue();
        ((Button)f.Window.FindName("OcrTestButton")).IsEnabled.ShouldBeTrue();
    });

    [Fact]
    public Task ReentrantClose_JoinsPublishedCleanupTask() => Run(async f =>
    {
        f.Show(); var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? nested = null;
        void Reenter(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(SettingsViewModel.CanEdit) || !f.Vm.IsStopped) return;
            f.Vm.PropertyChanged -= Reenter; nested = f.Session.ShutdownAsync();
        }
        f.Vm.PropertyChanged += Reenter;
        try
        {
            var closing = f.Track(f.Session.ShutdownAsync()); nested.ShouldBeSameAs(closing);
            gate.Open(); await work; await closing; f.Session.IsOpen.ShouldBeFalse();
        }
        finally { f.Vm.PropertyChanged -= Reenter; gate.Open(); }
    });

    [Theory][InlineData("edit")][InlineData("switch")][InlineData("delete")][InlineData("external")]
    public Task LateTestResult_IsRejectedAfterInputOrIdentityChanges(string change) => Run(async f =>
    {
        f.Vm.AddOcrProfile(OcrProviderKind.BaiduCloud); var target = f.Vm.SelectedOcrProfile!;
        var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "edit") target.DisplayName.Value = "edited";
        else if (change == "switch") f.Vm.SelectedOcrProfile = f.Vm.OcrProfiles[1];
        else if (change == "delete") f.Vm.DeleteOcrProfile();
        else
        {
            // Existing item with a matching ID must still invalidate the unsaved snapshot.
            f.Settings.External(s => s with { Ocr = s.Ocr with { Providers = [.. s.Ocr.Providers, new()
            { Id = target.Id, Kind = target.Kind, DisplayName = "external replacement", BaiduOptions = new() }] } });
            await f.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        gate.Open(); await work; target.TestMessage.ShouldBeNull();
        f.Vm.SelectedOcrProfile?.TestMessage.ShouldBeNull(); f.Cloud.Created.Single(p => p.Tests > 0).Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task Reload_WaitsForRealTestThenDiscardsSecretsAndDraft() => Run(async f =>
    {
        var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        var old = f.Vm.SelectedOcrProfile!; old.DisplayName.Value = "draft"; old.Credentials.Value = CredentialEditKind.Replace; old.SetReplacementCredentials("fake", "pair");
        var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Vm.ExecuteReload(); f.Vm.IsSaving.ShouldBeTrue(); f.Vm.SelectedOcrProfile.ShouldBeSameAs(old);
        gate.Open(); await work;
        for (int i = 0; i < 100 && f.Vm.IsSaving; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        f.Vm.IsSaving.ShouldBeFalse(); f.Vm.IsDirty.ShouldBeFalse(); old.ReplacementApiKey.ShouldBeNull();
        f.Settings.Updates.ShouldBe(0); f.Vm.SelectedOcrProfile.ShouldNotBeSameAs(old);
    });

    [Fact]
    public Task ClosingWhileSaveWaitsForOcr_PreventsCommitAndDrainsOriginalTask() => Run(async f =>
    {
        f.Show(); var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        f.Vm.Ocr.AutoCopyOnOcr.Value = true;
        var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = f.Track(f.Vm.ExecuteSaveAsync()); var close = f.Track(f.Session.ShutdownAsync());
        save.IsCompleted.ShouldBeFalse(); close.IsCompleted.ShouldBeFalse(); f.Settings.Updates.ShouldBe(0);
        gate.Open(); await work; await save; await close;
        f.Settings.Updates.ShouldBe(0); f.Session.IsOpen.ShouldBeFalse();
    });

    [Fact]
    public Task RepeatedOcrRequests_StayBoundedAndWaitForPriorCleanup() => Run(async f =>
    {
        var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 20; i++) await f.Vm.ExecuteOcrTestAsync();
        f.Cloud.Created.Count.ShouldBe(1); f.Cloud.Created.Single().Tests.ShouldBe(1);
        f.Vm.SelectedOcrProfile!.DisplayName.Value = "latest";
        var next = f.Track(f.Vm.ExecuteOcrTestAsync()); f.Cloud.Created.Count.ShouldBe(1);
        gate.Open(); await work; await next;
        f.Cloud.Created.Count.ShouldBe(2); f.Cloud.Created.ShouldAllBe(p => p.Disposed && p.Tests == 1);
    });

    [Fact]
    public Task ReentrantUiFailure_DoesNotLoseOriginalTestCleanup() => Run(async f =>
    {
        void Fail(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(SettingsViewModel.OcrTestStatus) || f.Vm.OcrTestStatus != UiText.OcrEnding) return;
            f.Vm.PropertyChanged -= Fail; throw new InvalidOperationException("synthetic UI failure");
        }
        f.Vm.PropertyChanged += Fail;
        try { await f.Vm.ExecuteOcrTestAsync(); }
        finally { f.Vm.PropertyChanged -= Fail; }
        f.Vm.HasOcrTestOwnership.ShouldBeFalse(); f.Cloud.Created.Single().Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task FullMultiPageSave_UsesOneCommitAndKeepsCredentialDomainsSeparate() => Run(async f =>
    {
        var cloud = f.Vm.SelectedOcrProfile!;
        cloud.Credentials.Value = CredentialEditKind.Replace; cloud.SetReplacementCredentials("ocr-api", "ocr-secret");
        f.Vm.SelectedProfile!.CredentialEditKind = CredentialEditKind.Replace; f.Vm.SelectedProfile.SetReplacementApiKey("translation-api");
        f.Vm.Theme = AppTheme.Dark; f.Vm.Ocr.AutoCopyOnOcr.Value = true; f.Vm.Screenshot.ShowMagnifier.Value = false;
        f.Vm.Selection.ClipboardTimeoutText = "850";
        await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(1, f.Vm.StatusMessage);
        var a = f.Settings.Value.Ocr.Providers.Single().CredentialSetId; var b = f.Settings.Value.Translation.Providers.Single().CredentialSetId;
        a.ShouldNotBe(b); f.SecretStore.Writes.ShouldBe(2); f.Settings.Value.Selection.ClipboardTimeoutMs.ShouldBe(850);
        f.Settings.Value.General.Theme.ShouldBe(AppTheme.Dark); f.Settings.Value.Ocr.AutoCopyOnOcr.ShouldBeTrue(); f.Settings.Value.Screenshot.ShowMagnifier.ShouldBeFalse();
    }, new AppSettings
    {
        Ocr = new() { Providers = [new() { Id = "same", Kind = OcrProviderKind.BaiduCloud, DisplayName = "OCR", BaiduOptions = new() }], ActiveProviderId = "same", FallbackProviderId = null },
        Translation = new() { Providers = [new() { Id = "same", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Translation", Model = "fake", BaseUrl = "https://fake.example.test", OpenAIOptions = new() }] }
    });

    [Fact]
    public Task Bootstrapper_ClosesSettingsBeforeRegistryAndRetainsFailedCleanupForRetry() => Run(async f =>
    {
        f.Show(); var gate = f.NewGate(); f.Cloud.Configure = p => { p.FailDispose = true; p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); }; };
        var work = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var owner = Substitute.For<ISettingsShutdownCoordinator>(); owner.ShutdownAsync(Arg.Any<CancellationToken>()).Returns(_ => f.Session.ShutdownAsync());
        var host = Substitute.For<IHost>(); int stops = 0, exits = 0;
        host.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { stops++; return Task.CompletedTask; });
        using var tray = new TrayIconController(Substitute.For<IAppLifetime>(), f.Settings, Substitute.For<IActionDispatcher>(), NullLogger<TrayIconController>.Instance) { SuppressIconCreationForTesting = true };
        var previous = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => exits++;
        var bootstrap = new AppBootstrapper(f.Settings, Substitute.For<ITranslationProviderRegistry>(), Substitute.For<IDatabaseInitializer>(),
            new LogLevelController(f.Settings, new Serilog.Core.LoggingLevelSwitch(), NullLogger<LogLevelController>.Instance), new ThemeService(f.Settings), tray,
            new TrayNotificationService(NullLogger<TrayNotificationService>.Instance), host, NullLogger<AppBootstrapper>.Instance, new(0), settingsShutdownCoordinator: owner, ocrRegistry: f.Registry);
        Task? close = null;
        try
        {
            close = f.Track(bootstrap.StopAsync(), typeof(IrisException)); stops.ShouldBe(0); close.IsCompleted.ShouldBeFalse();
            gate.Open(); await work; await Should.ThrowAsync<IrisException>(() => close); stops.ShouldBe(0); exits.ShouldBe(0); f.Window.IsVisible.ShouldBeTrue();
            f.Cloud.Created.Single().FailDispose = false; await bootstrap.StopAsync(); stops.ShouldBe(1); exits.ShouldBe(1); f.Session.IsOpen.ShouldBeFalse();
        }
        finally
        {
            gate.Open(); foreach (var p in f.Cloud.Created) p.FailDispose = false;
            if (close != null) try { await close; } catch (IrisException) { }
            try { await bootstrap.StopAsync(); } finally { AppBootstrapper.AppShutdowner = previous; }
        }
    });

    [Fact]
    public Task DisposalClearsPasswordBoxesAndProviderReferences() => Run(async f =>
    {
        var weak = await RunAndReleaseProvider(f);
        await f.Session.ShutdownAsync();
        ((PasswordBox)f.Window.FindName("OcrApiKeyPasswordBox")).Password.ShouldBeEmpty();
        ((PasswordBox)f.Window.FindName("OcrSecretKeyPasswordBox")).Password.ShouldBeEmpty();
        f.Vm.SelectedOcrProfile!.ReplacementApiKey.ShouldBeNull();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        weak.IsAlive.ShouldBeFalse(); GC.KeepAlive(f.Vm);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunAndReleaseProvider(OcrSettingsUiScope f)
    {
        f.Vm.SelectedOcrProfile!.Credentials.Value = CredentialEditKind.Replace;
        f.Vm.SelectedOcrProfile.SetReplacementCredentials(new string('x', 5000), "synthetic");
        await f.Vm.ExecuteOcrTestAsync(); var weak = new WeakReference(f.Cloud.Created.Single()); f.Cloud.Created.Clear(); return weak;
    }
}
