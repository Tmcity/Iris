using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Controls;
using Iris.App.Tests.Settings;
using Iris.Core.Network;
using Iris.Core.Settings;
using Shouldly;

namespace Iris.App.Tests.Updates;

public sealed class UpdatePreferenceUiTests
{
    [Fact]
    public async Task ClosingAWindowDoesNotAcquireAnUnrelatedAutomaticCleanupFailure()
    {
        using var directory = new Iris.App.Tests.TestSupport.TempDirectory();
        var source = new UpdateCoordinatorTests.Source { FailDispose = true };
        var runtime = new UpdateCoordinatorTests.Runtime(directory.Path, source);
        await NetworkSettingsUiScope.Run(async f =>
        {
            var coordinator = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Iris.App.Updates.AppUpdateCoordinator>(f.Services);
            coordinator.Start();
            try
            {
                await coordinator.CheckAsync(null, automatic: true);
                coordinator.Snapshot.Phase.ShouldBe(Iris.App.Updates.UpdatePhase.CleanupPending);
                await f.Session.ShutdownAsync();
                source.Disposed.ShouldBeFalse();
            }
            finally { source.FailDispose = false; await coordinator.StopAsync(); }
        }, NetworkSettingsUiScope.Initial() with { General = new() { CheckForUpdates = false } }, configure: services =>
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Iris.App.Updates.UpdateRuntime>(services, runtime);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Iris.App.Updates.AppUpdateCoordinator>(services);
        });
    }

    [Fact]
    public Task RealGeneralPreferenceIsJointlySavedAndRetryDoesNotSaveNewDrafts() => NetworkSettingsUiScope.Run(async f =>
    {
        f.Show(); f.Vm.CurrentPageKey = "general";
        f.Vm.CheckForUpdates.Value = false; f.Vm.InterfaceLanguage.Value = "en-US";
        f.Vm.History.MaxEntriesText.Value = "345"; f.Vm.Network.Mode.Value = ProxyMode.System;
        f.NetworkObserver.Fail = true;
        await f.Vm.ExecuteSaveAsync();
        f.Updates.ShouldBe(1); f.Receipts.PendingCount.ShouldBe(1);
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(f.SettingsFile, TestContext.Current.CancellationToken)))
        {
            json.RootElement.GetProperty("general").GetProperty("checkForUpdates").GetBoolean().ShouldBeFalse();
            json.RootElement.GetProperty("general").GetProperty("uiLanguage").GetString().ShouldBe("en-US");
            json.RootElement.GetProperty("history").GetProperty("maxEntries").GetInt32().ShouldBe(345);
        }
        f.Vm.CheckForUpdates.Value = true; f.NetworkObserver.Fail = false;
        await f.Vm.ExecuteRetryPendingApplyAsync();
        f.Updates.ShouldBe(1); f.Settings.Current.General.CheckForUpdates.ShouldBeFalse();
        f.Vm.CheckForUpdates.Value.ShouldBeTrue(); f.Vm.CheckForUpdates.IsDirty.ShouldBeTrue();
        f.Vm.ExecuteReload(); f.Vm.CheckForUpdates.Value.ShouldBeFalse(); f.Vm.IsDirty.ShouldBeFalse();
        f.Vm.CheckForUpdates.Value = true; f.Vm.ExecuteCancel();
        f.Settings.Current.General.CheckForUpdates.ShouldBeFalse(); f.Secrets.Writes.ShouldBe(0);
    });

    [Fact]
    public Task UpdatePreferenceOnlySaveHasNoApplyPending_AndSecondSaveIsNoOp() => NetworkSettingsUiScope.Run(async f =>
    {
        f.Vm.CheckForUpdates.Value = false; await f.Vm.ExecuteSaveAsync();
        f.Updates.ShouldBe(1); f.Receipts.PendingCount.ShouldBe(0); f.Vm.IsDirty.ShouldBeFalse();
        await f.Vm.ExecuteSaveAsync(); f.Updates.ShouldBe(1);
    });

    [Fact]
    public Task ReadonlyActualCheckboxDoesNotWritePreference() => NetworkSettingsUiScope.Run(async f =>
    {
        f.Show(); f.Vm.CurrentPageKey = "general"; await f.IdleAsync();
        ((CheckBox)f.Window.FindName("CheckForUpdatesCheckBox")).IsEnabled.ShouldBeFalse();
        f.Vm.CheckForUpdates.Value = false; f.Vm.CheckForUpdates.Value.ShouldBeTrue();
        await f.Vm.ExecuteSaveAsync(); f.Updates.ShouldBe(0);
    }, readOnly: true);

    [Fact]
    public Task ExternalMergeWaitsForActualCallbackThenConflictRetainsDraftUntilReload() => NetworkSettingsUiScope.Run(async f =>
    {
        var gate = f.NewGate(); var callbacks = new ConcurrentQueue<Task>();
        void Track(Task work) { callbacks.Enqueue(work); f.Track(work); }
        f.Ui.InvocationStarted += Track; f.Ui.PauseWhen(() => true, gate, after: false);
        try
        {
            await f.Settings.UpdateAsync(s => s with { General = s.General with { CheckForUpdates = false } }, TestContext.Current.CancellationToken);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            f.Settings.Current.General.CheckForUpdates.ShouldBeFalse(); f.Vm.CheckForUpdates.Value.ShouldBeTrue();
        }
        finally
        {
            gate.Open(); f.Ui.Clear();
            try { while (callbacks.TryDequeue(out var work)) await work; }
            finally { f.Ui.InvocationStarted -= Track; }
        }
        f.Vm.CheckForUpdates.Value.ShouldBeFalse(); f.Vm.CheckForUpdates.IsDirty.ShouldBeFalse();
        f.Vm.CheckForUpdates.Value = true; f.Vm.Theme = AppTheme.Dark;
        // A boolean target has only two values, so force a genuine conflict on another jointly edited field.
        await f.Settings.UpdateAsync(s => s with { General = s.General with { Theme = AppTheme.System } }, TestContext.Current.CancellationToken);
        await f.Vm.ExecuteSaveAsync();
        f.Vm.IsConflict.ShouldBeTrue(); f.Settings.Current.General.CheckForUpdates.ShouldBeFalse();
        f.Vm.CheckForUpdates.Value.ShouldBeTrue(); f.Vm.ExecuteReload(); f.Vm.CheckForUpdates.Value.ShouldBeFalse();
    });
}
