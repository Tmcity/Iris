using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Iris.Core.Actions;
using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Security;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Settings;

public sealed class SelectionSettingsEditTests
{
    private static SettingsEditPlan Plan(SelectionSettingsPatch patch) => new(new GeneralSettingsPatch(), []) { Selection = patch };

    [Fact]
    public Task FiveFields_NormalizeOnlyTargets_AndPreserveLatestMouseFields() => Run(async f =>
    {
        var original = f.Store.Current.Selection;
        f.Store.BeforeMutate = current => current with { Selection = current.Selection with { MouseMode = MouseSelectionMode.AutoTranslate, MinDragDistancePx = 42 } };
        var patch = new SelectionSettingsPatch(new(original.ReadOrder, [SelectionReadMethod.Clipboard]), new(true, false), new(400, 888),
            new(original.ClipboardUnsafeProcesses, [" new.EXE ", "NEW.exe", "helper.bin", "my app"]), new(true, false));
        var result = await f.Core.CommitAsync(Plan(patch));
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Updates.ShouldBe(1);
        var selection = f.Store.Current.Selection;
        selection.ReadOrder.ShouldBe(new[] { SelectionReadMethod.Clipboard }); selection.RestoreClipboard.ShouldBeFalse();
        selection.ClipboardTimeoutMs.ShouldBe(888); selection.OpenInputWhenEmpty.ShouldBeFalse();
        selection.ClipboardUnsafeProcesses.ShouldBe(new[] { "new.EXE", "helper.bin", "my app" });
        selection.MouseMode.ShouldBe(MouseSelectionMode.AutoTranslate); selection.MinDragDistancePx.ShouldBe(42);
        f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty(); result.PendingApply.ShouldBeNull();
        patch.ToString().ShouldBe("SelectionSettingsPatch"); Plan(patch).ToString().ShouldBe("SettingsEditPlan");
    });

    [Theory]
    [InlineData("orderNull")]
    [InlineData("orderEmpty")]
    [InlineData("orderDuplicate")]
    [InlineData("orderUnknown")]
    [InlineData("orderCount")]
    [InlineData("timeoutLow")]
    [InlineData("timeoutHigh")]
    [InlineData("namesNull")]
    [InlineData("nameNull")]
    [InlineData("nameEmpty")]
    [InlineData("nameLong")]
    [InlineData("namesCount")]
    public Task InvalidExplicitTargets_AreRejectedBeforeAnySideEffect(string scenario) => Run(async f =>
    {
        var patch = scenario switch
        {
            "orderNull" => new SelectionSettingsPatch(ReadOrder: new([], null!)),
            "orderEmpty" => new SelectionSettingsPatch(ReadOrder: new([], [])),
            "orderDuplicate" => new SelectionSettingsPatch(ReadOrder: new([], [SelectionReadMethod.Clipboard, SelectionReadMethod.Clipboard])),
            "orderUnknown" => new SelectionSettingsPatch(ReadOrder: new([], [(SelectionReadMethod)99])),
            "orderCount" => new SelectionSettingsPatch(ReadOrder: new([], [SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard])),
            "timeoutLow" => new SelectionSettingsPatch(ClipboardTimeoutMs: new(400, 99)),
            "timeoutHigh" => new SelectionSettingsPatch(ClipboardTimeoutMs: new(400, 2001)),
            "namesNull" => new SelectionSettingsPatch(ClipboardUnsafeProcesses: new([], null!)),
            "nameNull" => new SelectionSettingsPatch(ClipboardUnsafeProcesses: new([], [null!])),
            "nameEmpty" => new SelectionSettingsPatch(ClipboardUnsafeProcesses: new([], ["  "])),
            "nameLong" => new SelectionSettingsPatch(ClipboardUnsafeProcesses: new([], [new string('x', 256)])),
            _ => new SelectionSettingsPatch(ClipboardUnsafeProcesses: new([], Enumerable.Range(0, 257).Select(i => $"a{i}.exe").ToArray()))
        };
        var result = await f.Core.CommitAsync(MixedPlan(f) with { Selection = patch });
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.IsConflict.ShouldBeFalse(); result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument);
        f.Store.Updates.ShouldBe(0); f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty();
    });

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a.")]
    [InlineData("C:\\secret\\app.exe")]
    [InlineData("folder/app.exe")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    public Task InvalidFileNames_AreNotEchoedOrConvertedToBasenames(string name) => Run(async f =>
    {
        var result = await f.Core.CommitAsync(Plan(new(ClipboardUnsafeProcesses: new([], [name]))));
        result.Error!.Code.ShouldBe(ErrorCode.InvalidArgument); result.Error.Detail.ShouldBeNull(); result.Error.Message.ShouldNotContain(name);
        f.Store.Updates.ShouldBe(0);
    });

    [Fact]
    public Task CurrentAndBase_AreNotTrimmed_WhenRepairingHistoricalNames() => Run(async f =>
    {
        f.Store.Current = f.Store.Current with { Selection = f.Store.Current.Selection with { ClipboardUnsafeProcesses = [" cmd.exe "] } };
        var result = await f.Core.CommitAsync(Plan(new(ClipboardUnsafeProcesses: new([" cmd.exe "], [" cmd.exe "]))));
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Changes.ShouldBe(1);
        f.Store.Current.Selection.ClipboardUnsafeProcesses.ShouldBe(new[] { "cmd.exe" });
        var before = f.Store.Current;
        result = await f.Core.CommitAsync(Plan(new(ClipboardUnsafeProcesses: new(["unrelated"], ["CMD.EXE", "cmd.exe"]))));
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Current.ShouldBeSameAs(before); f.Store.Changes.ShouldBe(1);
    });

    [Fact]
    public Task HistoricalBadFields_ArePreservedUnlessExplicitlyRepaired() => Run(async f =>
    {
        f.Store.Current = f.Store.Current with { Selection = f.Store.Current.Selection with { ReadOrder = null!, ClipboardUnsafeProcesses = [null!, "bad/path"], ClipboardTimeoutMs = -1 } };
        var untouched = f.Store.Current.Selection;
        (await f.Core.CommitAsync(new(new GeneralSettingsPatch(LogLevel: new("Information", "Warning")), []))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Store.Current.Selection.ShouldBeSameAs(untouched);
        var patch = new SelectionSettingsPatch(new(null!, [SelectionReadMethod.UIAutomation]), ClipboardTimeoutMs: new(-1, 100), ClipboardUnsafeProcesses: new([null!, "bad/path"], []));
        (await f.Core.CommitAsync(Plan(patch))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Store.Current.Selection.ClipboardUnsafeProcesses.ShouldBeEmpty(); f.Store.Current.Selection.ReadOrder.Count.ShouldBe(1);
    });

    [Fact]
    public Task NullAndSpacedCurrent_AreNotEqualToEmptyOrTrimmedBase() => Run(async f =>
    {
        f.Store.Current = f.Store.Current with { Selection = f.Store.Current.Selection with { ClipboardUnsafeProcesses = [" cmd.exe "] } };
        (await f.Core.CommitAsync(Plan(new(ClipboardUnsafeProcesses: new(["cmd.exe"], ["new.exe"]))))).IsConflict.ShouldBeTrue();
        f.Store.Current = f.Store.Current with { Selection = f.Store.Current.Selection with { ClipboardUnsafeProcesses = null! } };
        (await f.Core.CommitAsync(Plan(new(ClipboardUnsafeProcesses: new([], ["new.exe"]))))).IsConflict.ShouldBeTrue();
        (await f.Core.CommitAsync(Plan(new(ClipboardUnsafeProcesses: new(null!, []))))).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Store.Current.Selection.ClipboardUnsafeProcesses.ShouldBeEmpty();
    });

    [Fact]
    public Task SelectionGeneralAndHotkeys_CommitOnceWithoutProviderSideEffects() => Run(async f =>
    {
        var original = f.Store.Current;
        var plan = new SettingsEditPlan(new GeneralSettingsPatch(ShowStartupNotification: new(true, false)), [])
        {
            Selection = new(ClipboardTimeoutMs: new(400, 100)),
            HotkeyChanges = [new(IrisAction.SelectionTranslate, original.Hotkeys[IrisAction.SelectionTranslate], new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "J", true))]
        };
        (await f.Core.CommitAsync(plan)).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Store.Updates.ShouldBe(1); f.Secrets.ReceivedCalls().ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FieldConflicts_AreCheckedBeforeStagingAndInsideMutate(bool duringMutate) => Run(async f =>
    {
        var plan = MixedPlan(f);
        if (duringMutate) f.Store.BeforeMutate = current => current with { Selection = current.Selection with { ClipboardTimeoutMs = 600 } };
        else f.Store.Current = f.Store.Current with { Selection = f.Store.Current.Selection with { ClipboardTimeoutMs = 600 } };
        var result = await f.Core.CommitAsync(plan);
        result.Status.ShouldBe(SettingsEditStatus.Uncommitted); result.IsConflict.ShouldBeTrue();
        f.Store.Current.Translation.Providers.ShouldBeEmpty(); f.Store.Current.General.LogLevel.ShouldBe("Information");
        f.SecretValues.ShouldBeEmpty(); f.Observer.ReceivedCalls().ShouldBeEmpty();
        f.Secrets.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Set").ShouldBe(duringMutate ? 1 : 0);
        f.Secrets.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Remove").ShouldBe(duringMutate ? 1 : 0);
    });

    [Fact]
    public Task BothLists_BaseAndTarget_AreCapturedBeforeAwaitingGate() => Run(async f =>
    {
        var entered = Gate(); var release = Gate(); f.Release.Add(() => release.TrySetResult());
        f.Store.BeforeUpdate = async _ => { entered.TrySetResult(); await release.Task; };
        var first = f.Track(f.Core.CommitAsync(new(new GeneralSettingsPatch(LogLevel: new("Information", "Warning")), [])));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var orderBase = f.Store.Current.Selection.ReadOrder.ToList(); var orderTarget = new List<SelectionReadMethod> { SelectionReadMethod.Clipboard };
        var namesBase = f.Store.Current.Selection.ClipboardUnsafeProcesses.ToList(); var namesTarget = new List<string> { " new.exe " };
        var second = f.Track(f.Core.CommitAsync(Plan(new(ReadOrder: new(orderBase, orderTarget), ClipboardUnsafeProcesses: new(namesBase, namesTarget)))));
        second.IsCompleted.ShouldBeFalse(); orderBase.Clear(); orderTarget.Clear(); namesBase.Clear(); namesTarget.Clear();
        f.Store.BeforeUpdate = null; release.TrySetResult(); await first;
        (await second).Status.ShouldBe(SettingsEditStatus.Applied);
        f.Store.Current.Selection.ReadOrder.ShouldBe(new[] { SelectionReadMethod.Clipboard });
        f.Store.Current.Selection.ClipboardUnsafeProcesses.ShouldBe(new[] { "new.exe" });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MixedPlan_IsOneUpdate_AndAcknowledgesPostCommitCancellation(bool cancelAfter) => Run(async f =>
    {
        using var cts = new CancellationTokenSource();
        if (cancelAfter) f.Store.AfterUpdate = () => cts.Cancel();
        var result = await f.Core.CommitAsync(MixedPlan(f), cts.Token);
        result.Status.ShouldBe(SettingsEditStatus.Applied); f.Store.Updates.ShouldBe(1); f.Store.Changes.ShouldBe(1);
        f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(800); f.Store.Current.General.LogLevel.ShouldBe("Warning");
        f.Store.Current.Hotkeys[IrisAction.SelectionTranslate].Key.ShouldBe("J"); f.Store.Current.Translation.Providers.Count.ShouldBe(1);
        f.SecretValues.Count.ShouldBe(1); f.Observer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "RefreshAsync").ShouldBe(1);
    });

    [Fact]
    public Task CancelAfterCredentialStaging_RollsBackWithoutSelectionWrite() => Run(async f =>
    {
        using var cts = new CancellationTokenSource();
        f.Secrets.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Do(_ => cts.Cancel());
        await Should.ThrowAsync<OperationCanceledException>(() => f.Core.CommitAsync(MixedPlan(f), cts.Token));
        f.Store.Updates.ShouldBe(0); f.Store.Current.Selection.ClipboardTimeoutMs.ShouldBe(400); f.SecretValues.ShouldBeEmpty();
    });

    private static SettingsEditPlan MixedPlan(Fixture f) => new(new GeneralSettingsPatch(LogLevel: new("Information", "Warning")),
        [new(TranslationProfileChangeKind.Add, null, new TranslationProviderProfile { Id = "tp-000000000023", DisplayName = "test", Kind = TranslationProviderKind.OpenAICompatible, BaseUrl = "https://example.invalid/v1", Model = "test" },
            new(CredentialEditKind.Replace, new ProviderCredentials("fixture-only", null)))], new([], ["tp-000000000023"]))
    {
        HotkeyChanges = [new(IrisAction.SelectionTranslate, f.Store.Current.Hotkeys[IrisAction.SelectionTranslate], new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "J", true))],
        Selection = new(ClipboardTimeoutMs: new(400, 800))
    };
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Run(Func<Fixture, Task> body)
    {
        var f = new Fixture(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in f.Release) release();
            foreach (var task in f.Tasks) try { await task; } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Fixture
    {
        public Store Store { get; } = new();
        public ISecretStore Secrets { get; } = Substitute.For<ISecretStore>();
        public ConcurrentDictionary<string, string> SecretValues { get; } = new();
        public IProviderConfigurationObserver Observer { get; } = Substitute.For<IProviderConfigurationObserver>();
        public SettingsEditCoordinator Core { get; }
        public List<Action> Release { get; } = [];
        public List<Task> Tasks { get; } = [];
        public T Track<T>(T task) where T : Task { Tasks.Add(task); return task; }
        public Fixture()
        {
            Secrets.Get(Arg.Any<string>()).Returns(call => SecretValues.GetValueOrDefault(call.Arg<string>()));
            Secrets.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Do(call => SecretValues[call.ArgAt<string>(0)] = call.ArgAt<string>(1));
            Secrets.Remove(Arg.Any<string>()).Returns(call => SecretValues.TryRemove(call.Arg<string>(), out _));
            var prompts = Substitute.For<IPromptTemplateProvider>(); prompts.Get(Arg.Any<string?>()).Returns(BuiltInPrompts.Default);
            Core = new(Store, Secrets, Substitute.For<ITranslationProviderRegistry>(), [Observer], prompts, TimeProvider.System, NullLogger<SettingsEditCoordinator>.Instance);
        }
    }
    private sealed class Store : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public int Updates { get; private set; }
        public int Changes { get; private set; }
        public Func<CancellationToken, Task>? BeforeUpdate { get; set; }
        public Func<AppSettings, AppSettings>? BeforeMutate { get; set; }
        public Action? AfterUpdate { get; set; }
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
        {
            if (BeforeUpdate != null) await BeforeUpdate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); Updates++;
            if (BeforeMutate != null) Current = BeforeMutate(Current);
            var old = Current; var next = mutate(Current);
            if (!ReferenceEquals(old, next)) { Current = next; Changes++; Changed?.Invoke(this, new(old, next)); }
            AfterUpdate?.Invoke();
        }
    }
}
