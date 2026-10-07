using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Iris.App.Settings;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Ocr;
using Iris.Core.Security;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

[Collection("AppShutdownerSharedState")]
public sealed class OcrSettingsUiTests
{
    [Fact]
    public Task OpeningAndNavigation_AreLazyAndPreserveDrafts() => Run(f =>
    {
        f.Show(); f.Vm.CurrentPageKey.ShouldBe("ocr"); f.Vm.IsDirty.ShouldBeFalse();
        ((ScrollViewer)f.Window.FindName("OcrPageScroll")).IsVisible.ShouldBeTrue();
        f.Vm.SelectedOcrProfile!.DisplayName.Value = "unsaved name";
        f.Vm.CurrentPageKey = "screenshot"; f.Window.UpdateLayout();
        ((ScrollViewer)f.Window.FindName("ScreenshotPageScroll")).IsVisible.ShouldBeTrue();
        f.Vm.CurrentPageKey = "ocr"; f.Vm.SelectedOcrProfile.DisplayName.Value.ShouldBe("unsaved name");
        f.SecretStore.Reads.ShouldBe(0); f.SecretStore.Writes.ShouldBe(0); f.Settings.Updates.ShouldBe(0);
        f.Cloud.Created.ShouldBeEmpty(); f.Local.Created.ShouldBeEmpty();
        return Task.CompletedTask;
    });

    [Fact]
    public Task AllOcrAndScreenshotFields_CommitWithGeneralInOneTransaction() => Run(async f =>
    {
        f.Show(); var draft = f.Vm.SelectedOcrProfile!;
        draft.DisplayName.Value = "custom cloud label"; draft.Enabled.Value = false;
        SetText(f, "OcrTimeoutTextBox", "120"); draft.Mode.Value = BaiduOcrMode.General;
        draft.DetectDirection.Value = false; draft.Paragraph.Value = false; draft.LanguageType.Value = "ENG"; draft.BaseUrl.Value = "https://ocr.example.test";
        f.Vm.Ocr.ActiveProviderId.Value = BuiltInProviderIds.WindowsOcr; f.Vm.Ocr.FallbackProviderId.Value = BuiltInProviderIds.BaiduOcr;
        f.Vm.Ocr.TextLayout.Value = OcrTextLayout.KeepLines; f.Vm.Ocr.LanguageHint.Value = " en "; f.Vm.Ocr.AutoCopyOnOcr.Value = true; f.Vm.Ocr.SilentOcrShowNotification.Value = false;
        f.Vm.SelectedOcrProfile = f.Vm.OcrProfiles[1]; f.Vm.SelectedOcrProfile.LanguageTag.Value = "en-US";
        f.Vm.CurrentPageKey = "screenshot"; SetText(f, "ScreenshotMaskOpacityTextBox", "0.9");
        f.Vm.Screenshot.ConfirmOnMouseUp.Value = false; f.Vm.Screenshot.ShowMagnifier.Value = false;
        f.Vm.Screenshot.HideIrisWindows.Value = false; f.Vm.Screenshot.CopyImageToClipboard.Value = true; f.Vm.LogLevel = "Warning";
        await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(1, f.Vm.StatusMessage); f.Vm.IsDirty.ShouldBeFalse();
        var value = f.Settings.Value; var cloud = value.Ocr.Providers[0];
        cloud.DisplayName.ShouldBe("custom cloud label"); cloud.Enabled.ShouldBeFalse(); cloud.TimeoutSeconds.ShouldBe(120);
        cloud.BaiduOptions.ShouldBe(new BaiduOcrOptions { Mode = BaiduOcrMode.General, DetectDirection = false, Paragraph = false, LanguageType = "ENG", BaseUrl = "https://ocr.example.test" });
        value.Ocr.Providers[1].WindowsOptions!.LanguageTag.ShouldBe("en-US"); value.Ocr.LanguageHint.ShouldBe("en");
        value.Ocr.ActiveProviderId.ShouldBe(BuiltInProviderIds.WindowsOcr); value.Ocr.FallbackProviderId.ShouldBe(BuiltInProviderIds.BaiduOcr);
        value.Ocr.TextLayout.ShouldBe(OcrTextLayout.KeepLines); value.Ocr.AutoCopyOnOcr.ShouldBeTrue(); value.Ocr.SilentOcrShowNotification.ShouldBeFalse();
        value.Screenshot.ShouldBe(new ScreenshotSettings { ConfirmOnMouseUp = false, ShowMagnifier = false, HideIrisWindows = false, CopyImageToClipboard = true, MaskOpacity = 0.9 });
        value.General.LogLevel.ShouldBe("Warning");
    });

    [Theory][InlineData("", true)][InlineData("121", true)][InlineData("abc", true)][InlineData("NaN", false)][InlineData("0.91", false)][InlineData("", false)]
    public Task SaveFlushesCurrentNumericText_RejectsInvalidWithoutUsingLastValidValue(string text, bool timeout) => Run(async f =>
    {
        f.Show(timeout ? "ocr" : "screenshot");
        string name = timeout ? "OcrTimeoutTextBox" : "ScreenshotMaskOpacityTextBox";
        var box = (TextBox)f.Window.FindName(name);
        string path = timeout ? "SelectedOcrProfile.TimeoutText.Value" : "Screenshot.MaskOpacityText.Value";
        BindingOperations.SetBinding(box, TextBox.TextProperty, new Binding(path) { Source = f.Vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        box.SetCurrentValue(TextBox.TextProperty, text);
        await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(0); f.Vm.IsDirty.ShouldBeTrue();
        if (timeout) { f.Vm.SelectedOcrProfile!.TimeoutText.Value.ShouldBe(text); f.Vm.SelectedOcrProfile.ErrorMessage.ShouldNotBeNull(); }
        else { f.Vm.Screenshot.MaskOpacityText.Value.ShouldBe(text); f.Vm.Screenshot.ErrorMessage.ShouldNotBeNull(); }
    });

    [Fact]
    public Task AddDelete_ProtectsBuiltInsAndRequiresExplicitPrimaryReplacement() => Run(async f =>
    {
        f.Show(); f.Vm.DeleteOcrProfile(); f.Vm.OcrProfiles.Count.ShouldBe(2);
        f.Vm.AddOcrProfile(OcrProviderKind.WindowsLocal); string id = f.Vm.SelectedOcrProfile!.Id;
        f.Vm.Ocr.ActiveProviderId.Value = id; f.Vm.Ocr.FallbackProviderId.Value = null;
        await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(1, f.Vm.StatusMessage);
        f.Vm.SelectedOcrProfile = f.Vm.OcrProfiles.Single(p => p.Id == id); f.Vm.DeleteOcrProfile();
        f.Vm.Ocr.ActiveProviderId.Value.ShouldBe(id); await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(1);
        f.Vm.Ocr.ErrorMessage.ShouldBe(UiText.OcrProviderSelectionError);
        f.Vm.Ocr.ActiveProviderId.Value = BuiltInProviderIds.BaiduOcr; await f.Vm.ExecuteSaveAsync();
        f.Settings.Value.Ocr.Providers.Select(p => p.Id).ShouldBe([BuiltInProviderIds.BaiduOcr, BuiltInProviderIds.WindowsOcr]);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task Credentials_PreserveOnUncommitted_ClearAfterCommittedOrPending(bool pending) => Run(async f =>
    {
        f.Show(); var p = f.Vm.SelectedOcrProfile!;
        ((PasswordBox)f.Window.FindName("OcrApiKeyPasswordBox")).Password.ShouldBeEmpty();
        p.Credentials.Value = CredentialEditKind.Replace;
        SetPasswords(f, "synthetic-api-key", "synthetic-secret-key");
        f.Settings.Failure = new IOException("synthetic failure"); await f.Vm.ExecuteSaveAsync();
        f.Settings.Updates.ShouldBe(0); p.ReplacementApiKey.ShouldBe("synthetic-api-key");
        ((PasswordBox)f.Window.FindName("OcrSecretKeyPasswordBox")).Password.ShouldBe("synthetic-secret-key");
        f.Settings.Failure = null; f.ExtraObserver.Fail = pending; await f.Vm.ExecuteSaveAsync();
        f.Settings.Updates.ShouldBe(1); f.Vm.IsDirty.ShouldBeFalse();
        f.Vm.SelectedOcrProfile!.Credentials.Value.ShouldBe(CredentialEditKind.Keep); p.ReplacementApiKey.ShouldBeNull(); p.ReplacementSecretKey.ShouldBeNull();
        ((PasswordBox)f.Window.FindName("OcrApiKeyPasswordBox")).Password.ShouldBeEmpty();
        JsonSerializer.Serialize(f.Settings.Value).ShouldNotContain("synthetic-api-key");
        if (pending)
        {
            f.Receipts.PendingCount.ShouldBe(1); int writes = f.SecretStore.Writes;
            f.ExtraObserver.Fail = false; f.Vm.SelectedOcrProfile.DisplayName.Value = "new unsaved name";
            await f.Vm.ExecuteRetryPendingApplyAsync(); f.Receipts.PendingCount.ShouldBe(0);
            f.Settings.Updates.ShouldBe(1); f.SecretStore.Writes.ShouldBe(writes); f.Vm.IsDirty.ShouldBeTrue();
        }
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task OcrAndScreenshotEdits_ReserveReceiptCapacityBeforeCommit(bool screenshot) => Run(async f =>
    {
        for (int i = 0; i < f.Receipts.MaxCapacity; i++)
        { f.Receipts.TryReserveSlot().ShouldBeTrue(); var receipt = Substitute.For<ISettingsApplyReceipt>(); receipt.CommitId.Returns(Guid.NewGuid()); f.Receipts.ConvertReservationToPending(receipt, "synthetic"); }
        if (screenshot) f.Vm.Screenshot.MaskOpacityText.Value = "0.7"; else f.Vm.Ocr.AutoCopyOnOcr.Value = true;
        await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(0); f.Vm.IsDirty.ShouldBeTrue();
        f.Vm.StatusMessage.ShouldBe(UiText.SettingsStatusPendingApplyQuotaFull); f.SecretStore.Writes.ShouldBe(0);
    });

    [Fact]
    public Task ExternalChanges_MergeCleanFieldsPreserveDirtyBaselineAndDoNotReviveDeletes() => Run(async f =>
    {
        var p = f.Vm.SelectedOcrProfile!; p.TimeoutText.Value = "88";
        f.Settings.External(s => s with { Ocr = s.Ocr with { LanguageHint = "ja", Providers = [s.Ocr.Providers[0] with { DisplayName = "externally changed" }, s.Ocr.Providers[1]] }, Screenshot = s.Screenshot with { ShowMagnifier = false } });
        await f.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        p.TimeoutText.Value.ShouldBe("88"); p.DisplayName.Value.ShouldNotBe("externally changed"); f.Vm.Ocr.LanguageHint.Value.ShouldBe("ja"); f.Vm.Screenshot.ShowMagnifier.Value.ShouldBeFalse();
        await f.Vm.ExecuteSaveAsync(); f.Vm.IsConflict.ShouldBeTrue(); f.Settings.Updates.ShouldBe(0);
        f.Vm.ExecuteReload(); f.Vm.SelectedOcrProfile!.DisplayName.Value.ShouldBe("externally changed"); f.Vm.IsDirty.ShouldBeFalse();
    });

    [Fact]
    public Task DraftTest_IgnoresUnrelatedInvalidScreenshotAndTestsDisabledUnsavedProfile() => Run(async f =>
    {
        f.Show(); f.Vm.AddOcrProfile(OcrProviderKind.BaiduCloud); var p = f.Vm.SelectedOcrProfile!;
        p.Enabled.Value = false; p.Credentials.Value = CredentialEditKind.Replace; SetPasswords(f, "fake-key", "fake-pair");
        f.Vm.Screenshot.MaskOpacityText.Value = "invalid";
        await f.Vm.ExecuteOcrTestAsync(); var provider = f.Cloud.Created.Single(x => x.Tests > 0);
        provider.Tests.ShouldBe(1); provider.Recognitions.ShouldBe(0); provider.Profile.Enabled.ShouldBeFalse(); provider.Credentials.ShouldBe(new ProviderCredentials("fake-key", "fake-pair"));
        provider.Disposed.ShouldBeTrue(); p.TestMessage.ShouldBe(UiText.OcrTestPassed); f.Settings.Updates.ShouldBe(0); f.SecretStore.Writes.ShouldBe(0);
        ((Button)f.Window.FindName("OcrCancelTestButton")).IsEnabled.ShouldBeFalse();
    });

    [Fact]
    public Task RunReturnedButDisposeFailed_RetainsOwnerAndClickableRetryDoesNotRetest() => Run(async f =>
    {
        f.Show(); f.Cloud.Configure = p => { p.FailDispose = true; p.Test = _ => Task.FromResult(new ProviderTestResult(false, TimeSpan.Zero, Error: new(ErrorCode.AuthenticationFailed, "private"))); };
        await f.Vm.ExecuteOcrTestAsync(); var provider = f.Cloud.Created.Single();
        f.Vm.HasFailedOcrCleanup.ShouldBeTrue(); provider.Disposed.ShouldBeFalse(); f.Vm.SelectedOcrProfile!.TestMessage.ShouldBe(ErrorMessages.GetDefault(ErrorCode.AuthenticationFailed));
        var retry = (Button)f.Window.FindName("OcrRetryCleanupButton"); f.Window.UpdateLayout(); retry.IsVisible.ShouldBeTrue(); retry.IsEnabled.ShouldBeTrue();
        f.Vm.Ocr.AutoCopyOnOcr.Value = true; await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(0);
        provider.FailDispose = false; retry.Command.Execute(null); await ((AsyncRelayCommand)retry.Command).ExecutionTask!;
        provider.Tests.ShouldBe(1); provider.Disposals.ShouldBe(2); provider.Disposed.ShouldBeTrue(); f.Vm.HasOcrTestOwnership.ShouldBeFalse();
        await f.Vm.ExecuteSaveAsync(); f.Settings.Updates.ShouldBe(1);
    });

    [Fact]
    public Task SaveWaitsForNonCooperativeTestAndFreezesPlan() => Run(async f =>
    {
        var gate = f.NewGate(); f.Cloud.Configure = p => p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); };
        f.Vm.SelectedOcrProfile!.DisplayName.Value = "frozen";
        var test = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = f.Track(f.Vm.ExecuteSaveAsync()); f.Vm.IsSaving.ShouldBeTrue(); f.Settings.Updates.ShouldBe(0); save.IsCompleted.ShouldBeFalse();
        f.Vm.SelectedOcrProfile.DisplayName.Value = "late edit"; f.Vm.SelectedOcrProfile.DisplayName.Value.ShouldBe("frozen");
        gate.Open(); await test; await save;
        f.Settings.Value.Ocr.Providers[0].DisplayName.ShouldBe("frozen"); f.Settings.Updates.ShouldBe(1);
    });

    [Fact]
    public Task CloseFailure_KeepsWindowAndOriginalSessionUntilClickableCleanupRetry() => Run(async f =>
    {
        f.Show(); var gate = f.NewGate(); f.Cloud.Configure = p => { p.FailDispose = true; p.Test = async _ => { await gate.Pause(); return new(true, TimeSpan.Zero); }; };
        var test = f.Track(f.Vm.ExecuteOcrTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Window.Close(); var close = f.Track(f.Session.CurrentCleanupTask!, typeof(InvalidOperationException));
        f.Window.IsVisible.ShouldBeTrue(); close.IsCompleted.ShouldBeFalse(); gate.Open(); await test;
        await Should.ThrowAsync<InvalidOperationException>(() => close); f.Window.IsVisible.ShouldBeTrue(); f.Vm.IsStopped.ShouldBeTrue();
        var provider = f.Cloud.Created.Single(); provider.FailDispose = false;
        var retry = (Button)f.Window.FindName("OcrRetryCleanupButton"); retry.Command.CanExecute(null).ShouldBeTrue(); retry.Command.Execute(null);
        await ((AsyncRelayCommand)retry.Command).ExecutionTask!; await f.Session.CurrentCleanupTask!;
        provider.Tests.ShouldBe(1); provider.Disposed.ShouldBeTrue(); new WindowInteropHelper(f.Window).Handle.ShouldBe(0);
    });

    [Fact]
    public Task SameId_OcrAndTranslationTasksDoNotCancelEachOther() => Run(async f =>
    {
        var ocrGate = f.NewGate(); var translationGate = f.NewGate(); CancellationToken ocrToken = default, translationToken = default;
        f.Cloud.Configure = p => p.Test = async token => { ocrToken = token; await ocrGate.Pause(); return new(true, TimeSpan.Zero); };
        f.Translation.Test = async token => { translationToken = token; await translationGate.Pause(); return new(true, TimeSpan.Zero); };
        var ocr = f.Track(f.Vm.ExecuteOcrTestAsync()); await ocrGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var translation = f.Track(f.Vm.ExecuteTestDraftAsync()); await translationGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ocrToken.IsCancellationRequested.ShouldBeFalse(); translationToken.IsCancellationRequested.ShouldBeFalse();
        ocrGate.Open(); translationGate.Open(); await ocr; await translation;
    }, new AppSettings
    {
        Ocr = new() { Providers = [new() { Id = "same", Kind = OcrProviderKind.BaiduCloud, DisplayName = "OCR", BaiduOptions = new() }], ActiveProviderId = "same", FallbackProviderId = null },
        Translation = new() { Providers = [new() { Id = "same", Kind = TranslationProviderKind.OpenAICompatible, DisplayName = "Translation", Model = "fake", BaseUrl = "https://fake.example.test", OpenAIOptions = new() }] }
    });

    private static void SetText(OcrSettingsUiScope f, string name, string text) => ((TextBox)f.Window.FindName(name)).SetCurrentValue(TextBox.TextProperty, text);
    private static void SetPasswords(OcrSettingsUiScope f, string key, string secret)
    { ((PasswordBox)f.Window.FindName("OcrApiKeyPasswordBox")).Password = key; ((PasswordBox)f.Window.FindName("OcrSecretKeyPasswordBox")).Password = secret; }
}
