using System.ComponentModel;
using Iris.App.Capture;
using Iris.App.History;
using Iris.App.Hosting;
using Iris.App.Translation;
using Iris.Core.Common;
using Iris.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using static Iris.App.Tests.History.HistoryTestScope;

namespace Iris.App.Tests.History;

[Collection("AppShutdownerSharedState")]
public sealed class HistoryLifecycleTests
{
    [Fact]
    public Task EndedWriteNotificationFailure_DoesNotReplayWriteOrBlockCleanup() => Run(async f =>
    {
        long id = await f.Repository.Inner.AddAsync(Entry("source")); await f.ShowAsync(); await f.SelectAsync();
        var vm = f.Vm;
        void FailFinalNotification(object? sender, PropertyChangedEventArgs args)
        {
            if (!vm.IsWriting && !f.Repository.Writes.IsEmpty) throw new InvalidOperationException("synthetic UI notification");
        }
        vm.PropertyChanged += FailFinalNotification; f.Undo.Add(() => vm.PropertyChanged -= FailFinalNotification);
        await Should.ThrowAsync<InvalidOperationException>(() => vm.ToggleFavoriteAsync());
        vm.PropertyChanged -= FailFinalNotification;
        (await f.Repository.Inner.GetAsync(id))!.IsFavorite.ShouldBeTrue();
        await f.Session.ShutdownAsync(); f.Repository.Writes.Count.ShouldBe(1); f.Presenter.ActiveSession.ShouldBeNull();
    });

    [Fact]
    public Task PartialShowWithFailedRollback_RetainsOriginalOwnerUntilRetry() => Run(async f =>
    {
        static void Reject(object? sender, CancelEventArgs args) => args.Cancel = true;
        f.ConfigureWindow = window => { window.Closing += Reject; f.Undo.Add(() => window.Closing -= Reject); };
        f.Placement.Fail = true;
        await Should.ThrowAsync<AggregateException>(() => f.Presenter.ShowAsync());
        var original = f.Session; original.IsDisposed.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => f.Presenter.ShowAsync());
        f.Presenter.ActiveSession.ShouldBeSameAs(original);
        original.Window!.Closing -= Reject; f.Placement.Fail = false; f.ConfigureWindow = null;
        await original.ShutdownAsync(); await f.ShowAsync(); f.Session.ShouldNotBeSameAs(original);
    });

    [Fact]
    public Task ClosedSession_ReleasesFullDetailWhileViewModelWitnessRemains() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry(new string('z', 5000))); await f.ShowAsync(); var vm = f.Vm;
        var weak = await ReadDetailReference(f); await f.Session.ShutdownAsync();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        weak.IsAlive.ShouldBeFalse(); vm.Detail.ShouldBeNull(); GC.KeepAlive(vm);
    });
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ReadDetailReference(HistoryTestScope f)
    { await f.SelectAsync(); return new(f.Vm.Detail!); }

    [Fact]
    public Task ReentrantCloseBeforeQueryAdmission_DoesNotStartWorkAfterStop() => Run(async f =>
    {
        await f.ShowAsync(); var vm = f.Vm; var session = f.Session;
        int queries = f.Repository.Queries.Count; Task? close = null;
        void Reenter(object? sender, PropertyChangedEventArgs args)
        {
            vm.PropertyChanged -= Reenter;
            close = f.Track(session.ShutdownAsync());
        }
        vm.PropertyChanged += Reenter; f.Undo.Add(() => vm.PropertyChanged -= Reenter);
        await vm.RefreshAsync(); await close!;
        f.Repository.Queries.Count.ShouldBe(queries); session.IsDisposed.ShouldBeTrue();
    });

    [Fact]
    public Task SearchReplacement_IsBoundedAndWaitsForNoncooperativeQuery() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("latest")); await f.ShowAsync();
        var gate = f.NewGate(); int calls = 0;
        f.Repository.QueryOverride = async (q, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { await gate.Pause(); return new([], 999); }
            return await f.Repository.Inner.QueryAsync(q, ct);
        };
        var first = f.Track(f.Vm.RefreshAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        f.Vm.Keyword = "discarded"; var middle = f.Track(f.Vm.QueryTask);
        f.Vm.Keyword = "latest"; var latest = f.Track(f.Vm.QueryTask);
        f.Time.Advance(TimeSpan.FromMilliseconds(300));
        middle.IsCanceled.ShouldBeTrue(); calls.ShouldBe(1); first.IsCompleted.ShouldBeFalse(); latest.IsCompleted.ShouldBeFalse();
        gate.Open(); await first; await latest;
        calls.ShouldBe(2); f.Vm.TotalCount.ShouldBe(1); f.Vm.Rows[0].Preview.ShouldBe("latest");
    });

    [Fact]
    public Task StaleDetailCannotReplaceNewSelection() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("first")); await f.Repository.Inner.AddAsync(Entry("second")); await f.ShowAsync();
        var gate = f.NewGate(); int calls = 0;
        f.Repository.GetOverride = async (id, ct) =>
        {
            var value = await f.Repository.Inner.GetAsync(id, CancellationToken.None);
            if (Interlocked.Increment(ref calls) == 1) await gate.Pause();
            return value;
        };
        f.Vm.SelectedRow = f.Vm.Rows[0]; var old = f.Track(f.Vm.DetailTask); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        f.Vm.SelectedRow = f.Vm.Rows[1]; var current = f.Track(f.Vm.DetailTask);
        calls.ShouldBe(1); f.Vm.Detail.ShouldBeNull();
        gate.Open(); await old; await current;
        f.Vm.Detail!.SourceText.ShouldBe("first"); f.Vm.Detail.Id.ShouldBe(f.Vm.SelectedRow!.Id);
    });

    [Theory][InlineData("copy")][InlineData("translate")]
    public Task CloseDuringGet_RejectsLateSideEffectAndWaitsForActualExit(string operation) => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("full source")); await f.ShowAsync(); await f.SelectAsync();
        var session = f.Session; var vm = f.Vm; var window = f.Window; var gate = f.NewGate();
        f.Repository.GetOverride = async (id, ct) => { var entry = await f.Repository.Inner.GetAsync(id, CancellationToken.None); await gate.Pause(); return entry; };
        var work = f.Track(operation == "copy" ? vm.CopySourceAsync() : vm.RetranslateAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var closing = f.Track(session.ShutdownAsync()); session.ShutdownAsync().ShouldBeSameAs(closing);
        closing.IsCompleted.ShouldBeFalse(); session.IsDisposed.ShouldBeFalse();
        gate.Open(); await work; await closing;
        f.ClipboardWrites.ShouldBeEmpty(); f.ProviderFactory.Requests.ShouldBeEmpty();
        session.IsDisposed.ShouldBeTrue(); window.DataContext.ShouldBeNull(); vm.Detail.ShouldBeNull(); vm.Rows.ShouldBeEmpty();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task NewActionOrDelete_RevokesGetBeforeTranslationAcceptance(bool delete) => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("source")); await f.ShowAsync(); await f.SelectAsync();
        var gate = f.NewGate(); int count = 0;
        f.Repository.GetOverride = async (id, ct) =>
        {
            var entry = await f.Repository.Inner.GetAsync(id, CancellationToken.None);
            if (++count == 1) await gate.Pause(); return entry;
        };
        var old = f.Track(f.Vm.RetranslateAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task replacement;
        if (delete) { f.Vm.RequestDelete(); replacement = f.Track(f.Vm.ConfirmAsync()); }
        else replacement = f.Track(f.Vm.CopySourceAsync());
        gate.Open(); await old; await replacement;
        f.ProviderFactory.Requests.ShouldBeEmpty(); f.ClipboardWrites.Count.ShouldBe(delete ? 0 : 1);
    });

    [Fact]
    public Task NoncooperativeClipboard_IsSerializedAndCloseWaitsForWrite() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("source")); await f.ShowAsync(); await f.SelectAsync();
        var gate = f.NewGate(); int calls = 0;
        f.BeforeClipboard = async (_, _) => { if (++calls == 1) await gate.Pause(); };
        var first = f.Track(f.Vm.CopySourceAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var next = f.Track(f.Vm.CopyResultAsync("old-a")); calls.ShouldBe(1);
        gate.Open(); await first; await next; f.ClipboardWrites.Last().ShouldBe("译文中文 % _ OR \"quoted\"");
        var closeGate = f.NewGate(); f.BeforeClipboard = (_, _) => closeGate.Pause();
        var write = f.Track(f.Vm.CopySourceAsync()); await closeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var close = f.Track(f.Session.ShutdownAsync()); close.IsCompleted.ShouldBeFalse();
        closeGate.Open(); await write; await close;
    });

    [Fact]
    public Task ActualClipboardAcceptance_RechecksIdentityAfterUiPause() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("source")); await f.ShowAsync(); await f.SelectAsync();
        var gate = f.NewGate(); f.Ui.PauseNext(gate);
        var copy = f.Track(f.Vm.CopySourceAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var close = f.Track(f.Session.ShutdownAsync()); close.IsCompleted.ShouldBeFalse();
        gate.Open(); await copy; await close; f.ClipboardWrites.ShouldBeEmpty();
    });

    [Fact]
    public Task WriteIsNotCanceledBySearch_AndCloseIncludesFinalUiPublication() => Run(async f =>
    {
        long id = await f.Repository.Inner.AddAsync(Entry("source")); await f.ShowAsync(); await f.SelectAsync();
        var gate = f.NewGate(); f.Repository.BeforeWrite = gate.Pause;
        var write = f.Track(f.Vm.ToggleFavoriteAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        f.Vm.Keyword = "other"; f.Time.Advance(TimeSpan.FromMilliseconds(300)); await f.Vm.QueryTask;
        f.Repository.Writes.Count.ShouldBe(1); await f.Vm.ToggleFavoriteAsync(); f.Repository.Writes.Count.ShouldBe(1);
        var uiGate = f.NewGate(); f.Ui.PauseNext(uiGate); gate.Open(); await uiGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        (await f.Repository.Inner.GetAsync(id, CancellationToken.None))!.IsFavorite.ShouldBeTrue(); write.IsCompleted.ShouldBeFalse();
        var close = f.Track(f.Session.ShutdownAsync()); close.IsCompleted.ShouldBeFalse();
        uiGate.Open(); await write; await close;
    });

    [Fact]
    public Task CloseFailureRetainsSessionAndRetryAllowsLaterNewWindow() => Run(async f =>
    {
        await f.ShowAsync(); var session = f.Session; var window = f.Window;
        static void Reject(object? s, CancelEventArgs e) => e.Cancel = true;
        window.Closing += Reject; f.Undo.Add(() => window.Closing -= Reject);
        await Should.ThrowAsync<InvalidOperationException>(() => session.ShutdownAsync());
        f.Presenter.ActiveSession.ShouldBeSameAs(session); session.IsDisposed.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => f.Presenter.ShowAsync());
        f.Presenter.ActiveSession.ShouldBeSameAs(session);
        window.Closing -= Reject; await session.ShutdownAsync();
        await f.ShowAsync(); f.Session.ShouldNotBeSameAs(session); f.Window.ShouldNotBeSameAs(window);
    });

    [Fact]
    public Task PartialShowFailureRollsBackAndShutdownBlocksNewShows() => Run(async f =>
    {
        f.Placement.Fail = true; await Should.ThrowAsync<Exception>(() => f.Presenter.ShowAsync());
        f.Presenter.ActiveSession.ShouldBeNull(); f.Placement.Fail = false;
        await f.ShowAsync(); var session = f.Session;
        await f.Presenter.ShutdownAsync(); await f.Presenter.ShowAsync();
        session.IsDisposed.ShouldBeTrue(); f.Presenter.ActiveSession.ShouldBeNull();
    });

    [Theory][InlineData("restore")][InlineData("show")][InlineData("close")][InlineData("reopen")]
    public Task ScreenshotReceipt_PreservesCurrentVisibilityIntent(string next) => Run(async f =>
    {
        int invalidated = 0;
        using (var unused = f.Presenter.ObservePreparation(() => invalidated++)) await unused.HideAsync();
        f.Presenter.ActiveSession.ShouldBeNull();
        await f.ShowAsync(); var window = f.Window;
        using var receipt = f.Host.Services.GetServices<IScreenshotPreparationParticipant>().Single().ObservePreparation(() => invalidated++);
        await receipt.HideAsync(); window.IsVisible.ShouldBeFalse();
        if (next == "show") { await f.Presenter.ShowAsync(); window.IsVisible.ShouldBeTrue(); }
        if (next is "close" or "reopen") await f.Session.ShutdownAsync();
        if (next == "reopen") await f.ShowAsync();
        await receipt.RestoreAsync(() => false);
        window.IsVisible.ShouldBe(next is not "close" and not "reopen");
        if (next == "reopen") { f.Window.ShouldNotBeSameAs(window); f.Window.IsVisible.ShouldBeTrue(); }
        if (next != "restore") invalidated.ShouldBeGreaterThan(0);
    });

    [Fact]
    public Task RealBootstrapper_WaitsForHistoryBeforeHostAndDispatcherExit() => Run(async f =>
    {
        await f.Repository.Inner.AddAsync(Entry("source")); await f.ShowAsync(); await f.SelectAsync();
        var gate = f.NewGate(); f.Repository.BeforeWrite = gate.Pause;
        var work = f.Track(f.Vm.ToggleFavoriteAsync()); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        int exits = 0; var previous = AppBootstrapper.AppShutdowner; AppBootstrapper.AppShutdowner = _ => exits++;
        f.Undo.Add(() => AppBootstrapper.AppShutdowner = previous);
        var stop = f.Track(f.Host.Services.GetRequiredService<AppBootstrapper>().StopAsync());
        stop.IsCompleted.ShouldBeFalse(); exits.ShouldBe(0);
        gate.Open(); await work; await stop; exits.ShouldBe(1); f.Presenter.ActiveSession.ShouldBeNull();
    });
}
