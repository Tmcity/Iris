using System.Net;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Data;
using Iris.Core.Network;
using Iris.Core.Settings;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using static Iris.App.Tests.Settings.NetworkSettingsUiScope;

namespace Iris.App.Tests.Settings;

[Collection("AppShutdownerSharedState")]
public sealed class NetworkSettingsUiTests
{
    [Fact]
    public Task ActualWindowJsonCoreAndTester_PasteCredentialsTestThenSaveWithoutLeakingPassword() => Run(async f =>
    {
        f.Show(); f.Select(); f.Wire.RequireAuth = true;
        f.Vm.Network.Mode.Value = ProxyMode.Custom;
        var url = (TextBox)f.Window.FindName("NetworkUrlTextBox");
        url.Text = "http://proxy-user:proxy-password@proxy.example.test:8080/";
        f.Vm.Network.Credentials.Value.ShouldBe(CredentialEditKind.Replace);
        f.Vm.Network.UserName.Value.ShouldBe("proxy-user");
        ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password.ShouldBe("proxy-password");
        f.Vm.Network.Url.Value.ShouldBe("http://proxy.example.test:8080/");
        url.Text.ShouldBe("http://proxy.example.test:8080/");
        await f.Vm.ExecuteNetworkTestAsync();
        f.Vm.NetworkResultText!.ShouldContain("HTTP 200"); f.Vm.NetworkResultText!.ShouldContain(UiText.NetworkViaProxy);
        f.Wire.Auth.ToArray().ShouldBe(["proxy-user:proxy-password"]);
        f.Updates.ShouldBe(0); f.Secrets.Writes.ShouldBe(0); f.Tester.SessionCount.ShouldBe(0); f.Vm.HasNetworkTestOwnership.ShouldBeFalse();
        f.Services.GetRequiredService<NetworkTransportManager>().HandlerCount.ShouldBe(0);
        f.Translation.Tests.ShouldBe(0); f.Cloud.Created.ShouldAllBe(p => p.Tests == 0 && p.Recognitions == 0);
        await f.Vm.ExecuteSaveAsync();
        f.Updates.ShouldBe(1); f.Vm.IsDirty.ShouldBeFalse(); f.Settings.Current.Network.ProxyMode.ShouldBe(ProxyMode.Custom);
        f.Settings.Current.Network.ProxyCredentialSetId.ShouldStartWith("pc-");
        (await File.ReadAllTextAsync(f.SettingsFile)).ShouldNotContain("proxy-password");
        f.Vm.Network.Credentials.Value.ShouldBe(CredentialEditKind.Keep); ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password.ShouldBeEmpty();
    });

    [Theory][InlineData("")][InlineData("http://user:password@bad host:8080/")][InlineData("socks5://proxy.example.test/path")]
    public Task CurrentInvalidTextBlocksTestAndWholeWindowSave_AndKeepsDraft(string invalid) => Run(async f =>
    {
        f.Show(); f.Select(); f.Vm.Network.Mode.Value = ProxyMode.Custom;
        f.Vm.Network.Credentials.Value = CredentialEditKind.Replace; f.Vm.Network.UserName.Value = "draft-user";
        ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password = "draft-secret";
        var text = (TextBox)f.Window.FindName("NetworkUrlTextBox");
        BindingOperations.SetBinding(text, TextBox.TextProperty, new Binding("Network.Url.Value") { Source = f.Vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        text.SetCurrentValue(TextBox.TextProperty, invalid); f.Vm.LogLevel = "Warning";
        await f.Vm.ExecuteNetworkTestAsync(); await f.Vm.ExecuteSaveAsync();
        f.Vm.Network.ErrorMessage.ShouldNotBeNull(); f.Vm.Network.Url.Value.ShouldBe(invalid);
        f.Vm.Network.ReplacementPassword.ShouldBe("draft-secret"); f.Transport.Creates.ShouldBe(0); f.Updates.ShouldBe(0); f.Secrets.Writes.ShouldBe(0);
    });

    [Fact]
    public Task ChangedAddressRequiresExplicitCredentialChoice_UncommittedKeepsPasswordAndReloadClearsIt() => Run(async f =>
    {
        f.Show(); f.Vm.Network.Url.Value = "http://proxy.example.test/"; f.Vm.Network.Mode.Value = ProxyMode.Custom;
        await f.Vm.ExecuteSaveAsync(); f.Vm.Network.ErrorMessage.ShouldBe(UiText.NetworkAddressNeedsCredentials); f.Updates.ShouldBe(0);
        f.Vm.Network.Credentials.Value = CredentialEditKind.Replace; f.Vm.Network.UserName.Value = "replacement";
        ((PasswordBox)f.Window.FindName("NetworkPasswordBox")).Password = "kept-on-conflict";
        await f.Settings.UpdateAsync(s => s with { Network = s.Network with { CustomProxyUrl = "http://external.example.test/" } });
        await f.IdleAsync(); await f.Vm.ExecuteSaveAsync();
        f.Vm.IsConflict.ShouldBeTrue(); f.Updates.ShouldBe(1); f.Vm.Network.ReplacementPassword.ShouldBe("kept-on-conflict");
        f.Vm.ExecuteReload(); f.Vm.Network.ReplacementPassword.ShouldBeEmpty(); f.Vm.Network.Url.Value.ShouldBe("http://external.example.test/");
    });

    [Theory][InlineData(ProxyMode.None)][InlineData(ProxyMode.System)]
    public Task UneditedInvalidDormantSettingsDoNotBlockModeSwitch(ProxyMode mode) => Run(async f =>
    {
        f.Show(); f.Vm.Network.Mode.Value = mode; await f.Vm.ExecuteSaveAsync();
        f.Settings.Current.Network.ProxyMode.ShouldBe(mode); f.Settings.Current.Network.CustomProxyUrl.ShouldBe("invalid dormant");
        f.Settings.Current.Network.ProxyCredentialSetId.ShouldBe("invalid-reference"); f.Updates.ShouldBe(1);
    }, Initial() with { Network = new() { ProxyMode = ProxyMode.Custom, CustomProxyUrl = "invalid dormant", ProxyCredentialSetId = "invalid-reference" } });

    [Fact]
    public Task MixedPages_SaveOnceShareOneReceipt_AndRetryDoesNotResubmitNewPassword() => Run(async f =>
    {
        f.Show(); f.Vm.Network.Mode.Value = ProxyMode.Custom; f.Vm.Network.Url.Value = "http://proxy.example.test/";
        f.Vm.Network.Credentials.Value = CredentialEditKind.Replace; f.Vm.Network.UserName.Value = "proxy"; f.Vm.Network.SetPassword("saved-proxy");
        f.Vm.LogLevel = "Warning"; f.Vm.History.MaxEntriesText.Value = "1234"; f.Vm.Screenshot.MaskOpacityText.Value = "0.5";
        f.Vm.Ocr.LanguageHint.Value = "en"; f.Vm.SelectedProfile!.DisplayName = "edited translation";
        f.Vm.SelectedOcrProfile!.DisplayName.Value = "edited OCR"; f.Vm.SelectedOcrProfile.Credentials.Value = CredentialEditKind.Replace;
        f.Vm.SelectedOcrProfile.SetReplacementCredentials("synthetic-api", "synthetic-secret");
        f.NetworkObserver.Fail = true;
        await f.Vm.ExecuteSaveAsync();
        f.Updates.ShouldBe(1); f.Receipts.PendingCount.ShouldBe(1); f.Receipts.InFlightReservations.ShouldBe(0);
        f.Settings.Current.History.MaxEntries.ShouldBe(1234); f.Settings.Current.General.LogLevel.ShouldBe("Warning");
        f.Settings.Current.Screenshot.MaskOpacity.ShouldBe(0.5); f.Settings.Current.Ocr.LanguageHint.ShouldBe("en");
        f.Vm.StatusMessage!.ShouldContain("已"); f.Vm.Network.ReplacementPassword.ShouldBeEmpty();
        int writes = f.Secrets.Writes;
        f.Vm.Network.Credentials.Value = CredentialEditKind.Replace; f.Vm.Network.SetPassword("unsaved-new-password");
        f.NetworkObserver.Fail = false; await f.Vm.ExecuteRetryPendingApplyAsync();
        f.Updates.ShouldBe(1); f.Secrets.Writes.ShouldBe(writes); f.Vm.Network.ReplacementPassword.ShouldBe("unsaved-new-password");
        f.Receipts.PendingCount.ShouldBe(0);
    });

    [Fact]
    public Task FullQuotaBlocksNetworkSaveBeforeStaging() => Run(async f =>
    {
        f.Show(); f.Vm.Network.Mode.Value = ProxyMode.System;
        for (int i = 0; i < f.Receipts.MaxCapacity; i++) f.Receipts.TryReserveSlot().ShouldBeTrue();
        await f.Vm.ExecuteSaveAsync(); f.Updates.ShouldBe(0); f.Vm.IsDirty.ShouldBeTrue(); f.Secrets.Writes.ShouldBe(0);
        while (f.Receipts.InFlightReservations > 0) f.Receipts.ReleaseReservation();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task EndpointReplacementOrDomainSwitchCancelsAndRejectsLateResults(bool switchDomain) => Run(async f =>
    {
        f.Show(); f.Select(); var gate = f.NewGate();
        f.Transport.Send = async (_, _) => { await gate.PauseAsync(); return new(HttpStatusCode.OK); };
        var test = f.Track(f.Vm.ExecuteNetworkTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (switchDomain) f.Select(NetworkEndpointKind.Ocr);
        else { await f.Settings.UpdateAsync(s => s with { Translation = s.Translation with { Providers = [s.Translation.Providers[0] with { BaseUrl = "http://new.example.test/" }] } }); await f.IdleAsync(); }
        test.IsCompleted.ShouldBeFalse(); gate.Open(); await test;
        f.Vm.NetworkResultText.ShouldBeNull(); f.Transport.Sends.ShouldBe(1); f.Tester.SessionCount.ShouldBe(0);
        f.Transport.Send = (request, _) => { request.RequestUri!.Host.ShouldBe(switchDomain ? "ocr.example.test" : "new.example.test"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); };
        await f.Vm.ExecuteNetworkTestAsync(); f.Vm.NetworkResultText!.ShouldContain("HTTP 401"); f.Vm.NetworkResultText!.ShouldContain(UiText.NetworkDirect);
    });

    [Fact]
    public Task LatestWaitingIntentReplacesPreviousAndSameRevisionDoesNotAccumulate() => Run(async f =>
    {
        f.Show(); f.Select(); var gate = f.NewGate();
        f.Transport.Send = async (_, _) => { if (f.Transport.Sends == 1) await gate.PauseAsync(); return new(HttpStatusCode.OK); };
        var a = f.Track(f.Vm.ExecuteNetworkTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Vm.Network.BypassLocal.Value = false; var b = f.Track(f.Vm.ExecuteNetworkTestAsync());
        await f.Vm.ExecuteNetworkTestAsync(); f.Transport.Creates.ShouldBe(1);
        f.Vm.Network.BypassLocal.Value = true; var c = f.Track(f.Vm.ExecuteNetworkTestAsync());
        gate.Open(); await Task.WhenAll(a, b, c); f.Transport.Sends.ShouldBe(2); f.Tester.SessionCount.ShouldBe(0);
        f.Vm.NetworkResultText!.ShouldContain("HTTP 200"); f.Updates.ShouldBe(0);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task SaveOrCloseWaitsForActualNoncooperativeHead(bool close) => Run(async f =>
    {
        f.Show(); f.Select(); var gate = f.NewGate(); f.Transport.Send = async (_, _) => { await gate.PauseAsync(); return new(HttpStatusCode.OK); };
        var test = f.Track(f.Vm.ExecuteNetworkTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Also exercise saving with no dirty fields: it must still drain the test.
        var action = f.Track(close ? f.Session.ShutdownAsync() : f.Vm.ExecuteSaveAsync());
        await f.IdleAsync(); action.IsCompleted.ShouldBeFalse(); test.IsCompleted.ShouldBeFalse(); f.Window.IsVisible.ShouldBeTrue();
        gate.Open(); await test; await action; f.Tester.SessionCount.ShouldBe(0); f.Updates.ShouldBe(0);
        f.Window.IsVisible.ShouldBe(!close);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task CleanupRetryIncludesLastUiPublicationBeforeSaveOrClose(bool close) => Run(async f =>
    {
        f.Show(); f.Select(); var content = new Content { Fail = true }; f.Restore.Add(() => content.Fail = false);
        f.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await f.Vm.ExecuteNetworkTestAsync(); f.Vm.HasFailedNetworkCleanup.ShouldBeTrue(); f.Tester.SessionCount.ShouldBe(1);
        content.Fail = false; var gate = f.NewGate();
        f.Ui.PauseWhen(() => !f.Vm.HasFailedNetworkCleanup && f.Tester.SessionCount == 0 && f.Vm.HasNetworkTestOwnership, gate);
        var retry = f.Track(f.Vm.RetryNetworkCleanupAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        retry.IsCompleted.ShouldBeFalse(); f.Vm.HasNetworkTestOwnership.ShouldBeTrue();
        f.Vm.History.Enabled.Value = false;
        var action = f.Track(close ? f.Session.ShutdownAsync() : f.Vm.ExecuteSaveAsync());
        await f.IdleAsync(); action.IsCompleted.ShouldBeFalse(); f.Updates.ShouldBe(0);
        gate.Open(); await retry; await action; f.Transport.Sends.ShouldBe(1); f.Tester.SessionCount.ShouldBe(0);
        f.Updates.ShouldBe(close ? 0 : 1);
    });

    [Fact]
    public Task ReadonlyDisablesNetworkEditsAndTestsWhileCloseStillWorks() => Run(async f =>
    {
        f.Show(); f.Settings.IsReadOnly.ShouldBeTrue(); f.Vm.CanEdit.ShouldBeFalse(); f.Vm.CanSave.ShouldBeFalse();
        f.Vm.TestNetworkCommand.CanExecute(null).ShouldBeFalse(); f.Vm.CancelCommand.CanExecute(null).ShouldBeTrue();
        await f.Session.ShutdownAsync(); f.Window.IsVisible.ShouldBeFalse(); f.Transport.Creates.ShouldBe(0);
    }, readOnly: true);

    [Theory][InlineData(false)][InlineData(true)]
    public Task CancelButtonRefreshesAfterCompletion_WhileCleanupPublicationRemainsOwned(bool cancel) => Run(async f =>
    {
        f.Show(); f.Select(); var send = f.NewGate(); var publication = f.NewGate();
        var button = (Button)f.Window.FindName("NetworkCancelButton");
        button.IsEnabled.ShouldBeFalse();
        f.Transport.Send = async (_, _) => { await send.PauseAsync(); return new(HttpStatusCode.OK); };
        var test = f.Track(f.Vm.ExecuteNetworkTestAsync()); await send.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        button.IsEnabled.ShouldBeTrue();
        if (cancel) f.Vm.CancelNetworkTest();
        f.Ui.PauseWhen(() => f.Tester.SessionCount == 0 && f.Vm.HasNetworkResult, publication);
        send.Open(); await publication.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        test.IsCompleted.ShouldBeFalse(); f.Vm.HasNetworkTestOwnership.ShouldBeTrue();
        publication.Open(); await test;
        f.Vm.CancelNetworkCommand.CanExecute(null).ShouldBeFalse(); button.IsEnabled.ShouldBeFalse();
        f.Vm.HasNetworkTestOwnership.ShouldBeFalse(); f.Transport.Sends.ShouldBe(1);
        if (cancel) f.Vm.NetworkResultText.ShouldBe(UiText.NetworkCanceled);
        else f.Vm.NetworkResultText!.ShouldContain("HTTP 200");
    });

    [Theory][InlineData("http://proxy-user@proxy.example.test/")][InlineData("http://proxy-user:@proxy.example.test/")]
    public Task UsernameOnlyPaste_SavesExplicitEmptyPassword(string address) => Run(async f =>
    {
        f.Show(); f.Vm.Network.Mode.Value = ProxyMode.Custom;
        ((TextBox)f.Window.FindName("NetworkUrlTextBox")).Text = address;
        f.Vm.Network.Credentials.Value.ShouldBe(CredentialEditKind.Replace); f.Vm.Network.UserName.Value.ShouldBe("proxy-user");
        await f.Vm.ExecuteSaveAsync(); f.Updates.ShouldBe(1);
        var json = System.Text.Json.JsonDocument.Parse(f.Secrets.Values[Iris.Core.Security.SecretKeys.ProxyCredentialSet(f.Settings.Current.Network.ProxyCredentialSetId!)]);
        using (json) json.RootElement.GetProperty("password").GetString().ShouldBe("");
    });

    [Fact]
    public Task ReloadWaitsForCleanupPublication_ThenClearsDraft() => Run(async f =>
    {
        f.Show(); f.Select(); var content = new Content { Fail = true }; f.Restore.Add(() => content.Fail = false);
        f.Transport.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await f.Vm.ExecuteNetworkTestAsync(); content.Fail = false;
        var gate = f.NewGate(); f.Ui.PauseWhen(() => !f.Vm.HasFailedNetworkCleanup && f.Tester.SessionCount == 0 && f.Vm.HasNetworkTestOwnership, gate);
        var retry = f.Track(f.Vm.RetryNetworkCleanupAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Vm.Network.Credentials.Value = CredentialEditKind.Replace; f.Vm.Network.SetPassword("unsubmitted");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) => { if (args.PropertyName == nameof(f.Vm.IsSaving) && !f.Vm.IsSaving) finished.TrySetResult(); };
        f.Vm.PropertyChanged += changed;
        try
        {
            f.Vm.ExecuteReload(); f.Vm.IsSaving.ShouldBeTrue(); f.Vm.Network.ReplacementPassword.ShouldBe("unsubmitted");
            gate.Open(); await retry; await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Vm.Network.ReplacementPassword.ShouldBeEmpty(); f.Transport.Sends.ShouldBe(1); f.Updates.ShouldBe(0);
        }
        finally { gate.Open(); f.Vm.PropertyChanged -= changed; }
    });

    [Fact]
    public Task PresenterClosesOldOwnerThenReopensFreshNetworkDraftWithoutLateResult() => Run(async f =>
    {
        await f.Session.ShutdownAsync(); var presenter = f.Services.GetRequiredService<Iris.App.Settings.SettingsWindowPresenter>();
        await presenter.ShowAsync("network"); var first = presenter.ActiveSession!;
        first.ViewModel.CurrentPageKey.ShouldBe("network");
        first.ViewModel.SelectedNetworkEndpoint = first.ViewModel.NetworkEndpoints.First();
        var gate = f.NewGate(); f.Transport.Send = async (_, _) => { await gate.PauseAsync(); return new(HttpStatusCode.OK); };
        var test = f.Track(first.ViewModel.ExecuteNetworkTestAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = f.Track(first.ShutdownAsync()); first.ShutdownAsync().ShouldBeSameAs(close);
        close.IsCompleted.ShouldBeFalse(); gate.Open(); await test; await close;
        await presenter.ShowAsync("network"); var second = presenter.ActiveSession!;
        second.ShouldNotBeSameAs(first); first.Window.IsVisible.ShouldBeFalse(); second.Window.IsVisible.ShouldBeTrue();
        second.ViewModel.NetworkResultText.ShouldBeNull(); second.ViewModel.Network.ReplacementPassword.ShouldBeEmpty();
        f.Transport.Sends.ShouldBe(1); await presenter.ShutdownAsync();
    });
}
