using System.Reflection;
using System.IO;
using Iris.App.Settings;
using Iris.App.Translation;
using Iris.Core.Errors;
using Iris.Core.Presentation;
using Iris.Core.Translation;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Workflows;

public sealed class SelectionOperationNotificationTests
{
    [Fact]
    public Task Notify_UsesRealImmediateTrayPathOnUiWithoutCreatingWindow() => SelectionEntryTestScope.Run(async s =>
    {
        var op = s.Begin();
        int calls = s.Ui.Calls;
        (await s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty)).ShouldBeTrue();
        s.Native.Sent.Count.ShouldBe(1);
        s.Native.Sent[0].ShouldBe((UiText.AppName, ErrorMessages.GetDefault(ErrorCode.SelectionEmpty), NotificationLevel.Warning, true));
        s.Ui.Calls.ShouldBe(calls + 1); s.WindowsCreated.ShouldBe(0); s.Presenter.IsVisible.ShouldBeFalse();
        ((TranslateWindowOperation)op).HasSessionOwnership.ShouldBeFalse(); op.Dispose();
        s.Presenter.ActiveOperationsCount.ShouldBe(0);
    });

    [Theory]
    [InlineData("notReady")]
    [InlineData("disposed")]
    [InlineData("nativeFailure")]
    public Task UnavailableTray_ReturnsFalseWithoutQueueOrWindow(string reason) => SelectionEntryTestScope.Run(async s =>
    {
        if (reason == "notReady") s.Native.IsCreated = false;
        if (reason == "disposed") s.Tray.Dispose();
        if (reason == "nativeFailure") s.Native.Failure = new IOException("private native failure");
        (await s.Presenter.TryNotifyAsync(s.Begin(), NotificationLevel.Warning, ErrorCode.SelectionEmpty)).ShouldBeFalse();
        s.Native.Sent.ShouldBeEmpty(); s.WindowsCreated.ShouldBe(0); PendingCount(s).ShouldBe(0);
    });

    [Fact]
    public Task NoTray_DoesNotEnterOrdinaryPendingQueue() => SelectionEntryTestScope.Run(async s =>
    {
        (await s.Presenter.TryNotifyAsync(s.Begin(), NotificationLevel.Warning, ErrorCode.SelectionEmpty)).ShouldBeFalse();
        PendingCount(s).ShouldBe(0); s.WindowsCreated.ShouldBe(0);
        s.Notifications.Notify(NotificationLevel.Info, "ordinary", "ordinary");
        PendingCount(s).ShouldBe(1); // The pre-existing ordinary notification queue still works.
    }, attachTray: false);

    [Theory]
    [InlineData("new")]
    [InlineData("hide")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("shutdown")]
    public Task QueuedNotification_RevalidatesAtActualUiAcceptance(string reason) => SelectionEntryTestScope.Run(async s =>
    {
        using var cts = new CancellationTokenSource();
        var op = s.Begin(cts.Token); var gate = s.Ui.GateNext();
        var notify = s.Track(Task.Run(() => s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty), CancellationToken.None));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task? shutdown = null;
        if (reason == "new") s.Begin();
        if (reason == "hide") await s.Presenter.HideAsync();
        if (reason == "cancel") { cts.Cancel(); await ((TranslateWindowOperation)op).CancelAsync(); }
        if (reason == "dispose") op.Dispose();
        if (reason == "shutdown") { op.Dispose(); shutdown = s.Track(s.Presenter.ShutdownAsync()); }
        ((TranslateWindowOperation)op).IsRetired.ShouldBeFalse();
        notify.IsCompleted.ShouldBeFalse(); if (shutdown != null) shutdown.IsCompleted.ShouldBeFalse();
        gate.Release.TrySetResult(); (await notify).ShouldBeFalse();
        if (shutdown != null) await shutdown;
        s.Native.Sent.ShouldBeEmpty(); s.WindowsCreated.ShouldBe(0);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NotifyAndShow_ShareSingleInFlightPublication(bool firstShow) => SelectionEntryTestScope.Run(async s =>
    {
        var op = s.Begin(); var gate = s.Ui.GateNext();
        var first = s.Track(firstShow ? s.Presenter.TryShowAsync(op, new TranslateWindowRequest { Text = "existing", AutoTranslate = false })
            : s.Presenter.TryNotifyAsync(op, NotificationLevel.Info, ErrorCode.SelectionEmpty));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<InvalidOperationException>(() => s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty));
        await Should.ThrowAsync<InvalidOperationException>(() => s.Presenter.TryShowAsync(op, new TranslateWindowRequest { Text = "other" }));
        gate.Release.TrySetResult(); (await first).ShouldBeTrue();
    });

    [Fact]
    public Task Notify_ValidatesOperationAndEnumsBeforeSideEffects() => SelectionEntryTestScope.Run(async s =>
    {
        var op = s.Begin(); var forged = Substitute.For<ITranslateWindowOperation>();
        await Should.ThrowAsync<ArgumentNullException>(() => s.Presenter.TryNotifyAsync(null!, NotificationLevel.Info, ErrorCode.SelectionEmpty));
        await Should.ThrowAsync<ArgumentException>(() => s.Presenter.TryNotifyAsync(forged, NotificationLevel.Info, ErrorCode.SelectionEmpty));
        var foreign = new TranslateWindowPresenter(s.Provider, s.Ui, s.Settings, s.Desktop, Microsoft.Extensions.Logging.Abstractions.NullLogger<TranslateWindowPresenter>.Instance);
        using var foreignOp = foreign.TryBeginOperation();
        await Should.ThrowAsync<ArgumentException>(() => s.Presenter.TryNotifyAsync(foreignOp!, NotificationLevel.Info, ErrorCode.SelectionEmpty));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => s.Presenter.TryNotifyAsync(op, (NotificationLevel)999, ErrorCode.SelectionEmpty));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => s.Presenter.TryNotifyAsync(op, NotificationLevel.Info, (ErrorCode)999));
        s.Native.Sent.ShouldBeEmpty(); s.Ui.Calls.ShouldBe(0); s.WindowsCreated.ShouldBe(0);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Notify_PreservesExistingVmSessionAndSubsequentRetry(bool ready) => SelectionEntryTestScope.Run(async s =>
    {
        var op = s.Begin();
        (await s.Presenter.TryShowAsync(op, new TranslateWindowRequest { Text = "existing text", Origin = TextOrigin.Selection, AutoTranslate = true })).ShouldBeTrue();
        await s.Orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await s.Presenter.ViewModel!.WaitForActiveWorkAsync();
        var internalOp = (TranslateWindowOperation)op;
        internalOp.HasSessionOwnership.ShouldBeTrue();
        s.Native.IsCreated = ready;
        (await s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty)).ShouldBe(ready);
        internalOp.HasSessionOwnership.ShouldBeTrue(); internalOp.ActiveWorkCount.ShouldBe(0);
        op.Dispose(); internalOp.IsRetired.ShouldBeFalse();
        s.Presenter.ViewModel.InputText.ShouldBe("existing text");
        var card = s.Presenter.ViewModel.Cards.Single(); card.CanRetry.ShouldBeTrue(); card.RetryCommand.Execute(null);
        await s.Presenter.ViewModel.WaitForActiveWorkAsync();
        s.Orchestrator.Requests.Count.ShouldBe(2); s.Orchestrator.Requests.Last().Origin.ShouldBe(TextOrigin.Selection);
    });

    [Fact]
    public Task UiDispatchFailure_IsSafeAndReturnsPublicationOwnership() => SelectionEntryTestScope.Run(async s =>
    {
        var op = s.Begin(); var gate = s.Ui.GateNext(); gate.Failure = new IOException("private dispatch failure");
        var task = s.Track(s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty), typeof(IrisException));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); op.Dispose(); gate.Release.TrySetResult();
        var error = await Should.ThrowAsync<IrisException>(() => task);
        error.InnerException.ShouldBeNull(); error.ToString().ShouldNotContain("private dispatch failure");
        s.Presenter.ActiveOperationsCount.ShouldBe(0); s.Native.Sent.ShouldBeEmpty();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDuringNotification_FinishesActualUiAndPreservesErrors(bool cleanupFailure)
    {
        var body = new InvalidOperationException("test body"); var cleanup = new IOException("test cleanup");
        SelectionEntryTestScope? observed = null;
        Task Scenario(SelectionEntryTestScope scope)
        {
            observed = scope;
            if (cleanupFailure) scope.AfterCleanup = () => throw cleanup;
            return NotifyGateScenario(scope, body);
        }
        if (cleanupFailure)
        {
            var error = await Should.ThrowAsync<AggregateException>(() => SelectionEntryTestScope.Run(Scenario));
            error.InnerExceptions.ShouldContain(body); error.InnerExceptions.ShouldContain(cleanup);
        }
        else (await Should.ThrowAsync<InvalidOperationException>(() => SelectionEntryTestScope.Run(Scenario))).ShouldBeSameAs(body);
        observed!.Presenter.ActiveOperationsCount.ShouldBe(0); observed.Tasks.ShouldAllBe(t => t.Task.IsCompleted);
    }

    [Fact]
    public Task NotifyGate_NormalPathCompletes() => SelectionEntryTestScope.Run(s => NotifyGateScenario(s, null));

    private static async Task NotifyGateScenario(SelectionEntryTestScope s, Exception? injected)
    {
        var op = s.Begin(); var gate = s.Ui.GateNext();
        var notify = s.Track(s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); notify.IsCompleted.ShouldBeFalse();
        if (injected != null) throw injected;
        gate.Release.TrySetResult(); (await notify).ShouldBeTrue();
    }

    [Fact]
    public Task CallerDisposedDuringNotification_HoldsCapacityUntilUiCompletes() => SelectionEntryTestScope.Run(async s =>
    {
        var op = s.Begin(); var gate = s.Ui.GateNext();
        var notify = s.Track(s.Presenter.TryNotifyAsync(op, NotificationLevel.Warning, ErrorCode.SelectionEmpty));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); op.Dispose();
        for (int i = 1; i < TranslateWindowPresenter.MaxActiveOperations; i++) s.Begin().ShouldNotBeNull();
        s.Presenter.TryBeginOperation().ShouldBeNull();
        gate.Release.TrySetResult(); (await notify).ShouldBeFalse();
        s.Begin().ShouldNotBeNull();
    });

    private static int PendingCount(SelectionEntryTestScope s)
    {
        var queue = s.Notifications.GetType().GetField("_pendingNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Notifications)!;
        return (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
    }
}
