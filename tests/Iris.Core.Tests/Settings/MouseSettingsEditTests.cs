using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Settings;

public sealed class MouseSettingsEditTests
{
    [Fact]
    public async Task FiveMouseFields_CommitOnceNormalizeTargetsAndLeaveUneditedFields()
    {
        using var f = new Fixture();
        f.Store.BeforeMutate = current => current with { Selection = current.Selection with { ClipboardTimeoutMs = 999 } };
        var patch = new SelectionSettingsPatch { MouseMode = new(MouseSelectionMode.Off, MouseSelectionMode.ShowIcon), RequireIBeamCursor = new(true, false),
            MinDragDistancePx = new(6, 12), IconAutoHideMs = new(3000, 8000), ExcludedProcesses = new([], [" a.EXE ", "A.exe", "my app"]) };
        var result = await f.Core.CommitAsync(new(new GeneralSettingsPatch(ShowStartupNotification: new(true, false)), []) { Selection = patch }, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Updates.ShouldBe(1);
        var actual = f.Store.Current.Selection;
        actual.MouseMode.ShouldBe(MouseSelectionMode.ShowIcon); actual.RequireIBeamCursor.ShouldBeFalse(); actual.MinDragDistancePx.ShouldBe(12);
        actual.IconAutoHideMs.ShouldBe(8000); actual.ExcludedProcesses.ShouldBe(new[] { "a.EXE", "my app" }); actual.ClipboardTimeoutMs.ShouldBe(999);
        f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty(); result.PendingApply.ShouldBeNull();
    }
    [Theory]
    [InlineData("mode")][InlineData("dragLow")][InlineData("dragHigh")][InlineData("hideLow")][InlineData("hideHigh")][InlineData("names")]
    public async Task InvalidTargets_RejectBeforeUpdate(string field)
    {
        using var f = new Fixture();
        var patch = field switch
        {
            "mode" => new SelectionSettingsPatch { MouseMode = new(MouseSelectionMode.Off, (MouseSelectionMode)99) },
            "dragLow" => new() { MinDragDistancePx = new(6, 1) }, "dragHigh" => new() { MinDragDistancePx = new(6, 51) },
            "hideLow" => new() { IconAutoHideMs = new(3000, 499) }, "hideHigh" => new() { IconAutoHideMs = new(3000, 10001) },
            _ => new() { ExcludedProcesses = new([], ["C:\\private.exe"]) },
        };
        var result = await f.Core.CommitAsync(new(new(), []) { Selection = patch }, TestContext.Current.CancellationToken);
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument); f.Store.Updates.ShouldBe(0);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ProcessBaseIsNotTrimmed_AndBothConflictPhasesAreEnforced(bool late)
    {
        using var f = new Fixture();
        var conflicting = f.Store.Current with { Selection = f.Store.Current.Selection with { ExcludedProcesses = [" editor.exe "] } };
        if (late) f.Store.BeforeMutate = _ => conflicting; else f.Store.Current = conflicting;
        var result = await f.Core.CommitAsync(new(new(), []) { Selection = new() { ExcludedProcesses = new(["editor.exe"], ["other.exe"]) } }, TestContext.Current.CancellationToken);
        result.IsConflict.ShouldBeTrue(); result.Status.ShouldBe(SettingsEditStatus.Uncommitted);
    }
    [Fact]
    public async Task ListCaptureBeforeWait_IsIndependent_AndUneditedBadValuesDoNotBlock()
    {
        using var f = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.BeforeUpdate = async () => { entered.TrySetResult(); await release.Task; };
        var baseline = new List<string>(); var target = new List<string> { "original.exe" };
        Task<SettingsEditResult>? commit = null;
        try
        {
            commit = f.Core.CommitAsync(new(new(), []) { Selection = new() { ExcludedProcesses = new(baseline, target) } }, TestContext.Current.CancellationToken);
            await entered.Task; baseline.Add("changed.exe"); target.Clear(); release.TrySetResult();
            (await commit).Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Current.Selection.ExcludedProcesses.ShouldBe(new[] { "original.exe" });
        }
        finally { release.TrySetResult(); if (commit != null) await commit; }
        f.Store.Current = f.Store.Current with { Selection = f.Store.Current.Selection with { MinDragDistancePx = -1 } };
        (await f.Core.CommitAsync(new(new(ShowStartupNotification: new(true, false)), []), TestContext.Current.CancellationToken)).Status.ShouldBe(SettingsEditStatus.Applied);
    }
    [Fact]
    public async Task Toggle_UsesLatestAtomicValueAndOnlyRemembersSavedNonOffInThisInstance()
    {
        var store = new Store(); using var handler = new ToggleMouseSelectionHandler(store);
        await handler.ExecuteAsync(new(ActionTrigger.Hotkey), TestContext.Current.CancellationToken);
        store.Current.Selection.MouseMode.ShouldBe(MouseSelectionMode.ShowIcon);
        await store.UpdateAsync(s => s with { Selection = s.Selection with { MouseMode = MouseSelectionMode.AutoTranslate, IconAutoHideMs = 9000 } }, TestContext.Current.CancellationToken);
        await handler.ExecuteAsync(new(ActionTrigger.Hotkey), TestContext.Current.CancellationToken); store.Current.Selection.MouseMode.ShouldBe(MouseSelectionMode.Off);
        await handler.ExecuteAsync(new(ActionTrigger.Hotkey), TestContext.Current.CancellationToken); store.Current.Selection.MouseMode.ShouldBe(MouseSelectionMode.AutoTranslate);
        store.Current.Selection.IconAutoHideMs.ShouldBe(9000); store.Updates.ShouldBe(4);
        store.Current.Hotkeys.ContainsKey(IrisAction.ToggleMouseSelection).ShouldBeFalse();
        store.BeforeMutate = s => s with { Selection = s.Selection with { MouseMode = MouseSelectionMode.ShowIcon } };
        await handler.ExecuteAsync(new(ActionTrigger.Hotkey), TestContext.Current.CancellationToken); store.Current.Selection.MouseMode.ShouldBe(MouseSelectionMode.Off);
    }
    private sealed class Fixture : IDisposable
    {
        public void Dispose() => Core.Dispose();
        internal readonly Store Store = new();
        internal readonly ISecretStore Secrets = Substitute.For<ISecretStore>();
        internal readonly IProviderConfigurationObserver Observer = Substitute.For<IProviderConfigurationObserver>();
        internal readonly SettingsEditCoordinator Core;
        internal Fixture()
        {
            var prompts = Substitute.For<IPromptTemplateProvider>(); prompts.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);
            Core = new(Store, Secrets, Substitute.For<ITranslationProviderRegistry>(), [Observer], prompts, TimeProvider.System, NullLogger<SettingsEditCoordinator>.Instance);
        }
    }
    private sealed class Store : ISettingsService
    {
        public bool IsReadOnly => false;
        public AppSettings Current { get; set; } = new();
        internal int Updates;
        internal Func<AppSettings, AppSettings>? BeforeMutate;
        internal Func<Task>? BeforeUpdate;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            if (BeforeUpdate != null) await BeforeUpdate(); cancellationToken.ThrowIfCancellationRequested(); Updates++;
            if (BeforeMutate != null) Current = BeforeMutate(Current);
            var old = Current; Current = mutate(Current); Changed?.Invoke(this, new(old, Current));
        }
    }
}
