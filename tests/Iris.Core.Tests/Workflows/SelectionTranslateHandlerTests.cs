using System.Runtime.ExceptionServices;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Platform;
using Iris.Core.Presentation;
using Iris.Core.Settings;
using Iris.Core.Translation;
using Iris.Core.Workflows;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Iris.Core.Tests.Workflows;

public sealed class SelectionTranslateHandlerTests
{
    [Fact]
    public Task ConstructorAndRejectedAdmission_DoNotReadAnything() => Run(async s =>
    {
        s.SettingsReads.ShouldBe(0); s.Reader.ReceivedCalls().ShouldBeEmpty(); s.Desktop.ReceivedCalls().ShouldBeEmpty();
        s.Presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns((ITranslateWindowOperation?)null);
        await s.Handler.ExecuteAsync(s.Context, CancellationToken.None);
        s.SettingsReads.ShouldBe(0); s.Reader.ReceivedCalls().ShouldBeEmpty();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => s.Handler.ExecuteAsync(s.Context, canceled.Token));
        s.Presenter.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "TryBeginOperation").ShouldBe(1);
    });

    [Fact]
    public Task SelfForeground_UsesSameOperationAndPreservesInputWithoutToggle() => Run(async s =>
    {
        s.Desktop.GetForegroundApp().Returns(new ForegroundAppInfo("Iris.exe", Environment.ProcessId, "private title", 1));
        s.Presenter.IsVisible.Returns(true); s.Presenter.IsActive.Returns(true);
        await s.Handler.ExecuteAsync(s.Context, CancellationToken.None);
        var request = s.Shown.ShouldNotBeNull();
        request.Text.ShouldBeNull(); request.Origin.ShouldBe(TextOrigin.Input); request.AutoTranslate.ShouldBeFalse(); request.FocusInput.ShouldBeTrue();
        request.Anchor.ShouldBe(s.Context.Anchor); s.ShownOperation.ShouldBeSameAs(s.Operation);
        s.Reader.ReceivedCalls().ShouldBeEmpty(); await s.Presenter.DidNotReceive().HideAsync();
        s.Operation.Received(1).Dispose(); s.SettingsReads.ShouldBe(1);
    });

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" \r\n ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(" original\r\n text ", false)]
    public Task ResultBranches_UseSafeRequestsOrOperationNotification(string? text, bool openInput) => Run(async s =>
    {
        s.Current = s.Current with { Selection = s.Current.Selection with { OpenInputWhenEmpty = openInput } };
        s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), s.OperationToken.Token).Returns(Task.FromResult(text == null ? null : new SelectionReadResult(text, SelectionReadMethod.Clipboard, null)));
        await s.Handler.ExecuteAsync(s.Context, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(text) && !openInput)
        {
            s.Shown.ShouldBeNull();
            await s.Presenter.Received(1).TryNotifyAsync(s.Operation, NotificationLevel.Warning, ErrorCode.SelectionEmpty);
        }
        else
        {
            var request = s.Shown.ShouldNotBeNull(); request.Origin.ShouldBe(TextOrigin.Selection); request.Anchor.ShouldBe(s.Context.Anchor);
            request.Text.ShouldBe(string.IsNullOrWhiteSpace(text) ? "" : text);
            request.AutoTranslate.ShouldBe(!string.IsNullOrWhiteSpace(text)); request.FocusInput.ShouldBe(string.IsNullOrWhiteSpace(text));
            if (string.IsNullOrWhiteSpace(text)) request.Error.ShouldBe(new ErrorInfo(ErrorCode.SelectionEmpty, ErrorMessages.GetDefault(ErrorCode.SelectionEmpty)));
        }
        s.Operation.Received(1).Dispose();
    });

    [Fact]
    public Task SettingsSnapshot_IsCapturedOnceAndBothListsAreIndependent() => Run(async s =>
    {
        var order = new List<SelectionReadMethod> { SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation };
        var excluded = new List<string> { "terminal.exe" };
        s.Current = s.Current with { Selection = s.Current.Selection with { ReadOrder = order, ClipboardUnsafeProcesses = excluded, ClipboardTimeoutMs = 777, OpenInputWhenEmpty = true } };
        var entered = Gate(); var release = Gate(); s.Release.Add(() => release.TrySetResult());
        SelectionReadOptions? observed = null;
        s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observed = call.Arg<SelectionReadOptions>(); call.Arg<CancellationToken>().ShouldBe(s.OperationToken.Token);
            entered.TrySetResult(); await release.Task; return null;
        });
        var work = s.Track(s.Handler.ExecuteAsync(s.Context, CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        order.Clear(); excluded.Clear(); s.Current = new AppSettings { Selection = new SelectionSettings { OpenInputWhenEmpty = false } };
        s.Operation.DidNotReceive().Dispose(); release.TrySetResult(); await work;
        observed!.Order.ShouldBe(new[] { SelectionReadMethod.Clipboard, SelectionReadMethod.UIAutomation });
        observed.ClipboardUnsafeProcesses.ShouldBe(new[] { "terminal.exe" }); observed.ClipboardTimeoutMs.ShouldBe(777);
        s.SettingsReads.ShouldBe(1); s.Shown!.Text.ShouldBe(""); s.Shown.FocusInput.ShouldBeTrue(); s.Operation.Received(1).Dispose();
    });

    [Theory]
    [InlineData("settings", true)]
    [InlineData("settings", false)]
    [InlineData("desktop", true)]
    [InlineData("desktop", false)]
    [InlineData("reader", true)]
    [InlineData("reader", false)]
    public Task PreflightErrors_AreSanitizedWithoutSensitiveLogs(string phase, bool known) => Run(async s =>
    {
        Exception failure = known ? new IrisException(ErrorCode.CaptureFailed, "private text", new IOException("private inner"), detail: "private detail") : new IOException("private text");
        if (phase == "settings") s.Settings.Current.Returns(_ => throw failure);
        if (phase == "desktop") s.Desktop.GetForegroundApp().Returns(_ => throw failure);
        if (phase == "reader") s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns<Task<SelectionReadResult?>>(_ => throw failure);
        await s.Handler.ExecuteAsync(s.Context, CancellationToken.None);
        var request = s.Shown.ShouldNotBeNull(); var code = known ? ErrorCode.CaptureFailed : ErrorCode.Unknown;
        request.Text.ShouldBeNull(); request.Origin.ShouldBe(TextOrigin.Selection); request.FocusInput.ShouldBeFalse(); request.AutoTranslate.ShouldBeFalse();
        request.Error.ShouldBe(new ErrorInfo(code, ErrorMessages.GetDefault(code)));
        s.Log.Messages.ShouldAllBe(message => !message.Contains("private", StringComparison.Ordinal)); s.Log.Exceptions.ShouldAllBe(ex => ex == null);
        s.Operation.Received(1).Dispose();
    });

    [Fact]
    public Task ReaderCancellation_QuietlyReturnsOperation() => Run(async s =>
    {
        s.Reader.ReadAsync(Arg.Any<SelectionReadOptions>(), Arg.Any<CancellationToken>()).Returns<Task<SelectionReadResult?>>(_ => throw new OperationCanceledException());
        await s.Handler.ExecuteAsync(s.Context, CancellationToken.None);
        s.Shown.ShouldBeNull(); await s.Presenter.DidNotReceive().TryNotifyAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<NotificationLevel>(), Arg.Any<ErrorCode>());
        s.Operation.Received(1).Dispose();
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Publication_IsAwaitedAndNeverRetriedOrRecursivelyReported(bool notify, bool throws) => Run(async s =>
    {
        s.Current = s.Current with { Selection = s.Current.Selection with { OpenInputWhenEmpty = !notify } };
        var entered = Gate(); var release = Gate(); s.Release.Add(() => release.TrySetResult());
        async Task<bool> Publish()
        {
            entered.TrySetResult(); await release.Task;
            if (throws) throw new IOException("publication failure");
            return false;
        }
        if (notify) s.Presenter.TryNotifyAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<NotificationLevel>(), Arg.Any<ErrorCode>()).Returns(_ => Publish());
        else s.Presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(_ => Publish());
        var work = s.Track(s.Handler.ExecuteAsync(s.Context, CancellationToken.None), throws ? typeof(IOException) : null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); s.Operation.DidNotReceive().Dispose(); work.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        if (throws) await Should.ThrowAsync<IOException>(() => work); else await work;
        s.Presenter.ReceivedCalls().Count(c => c.GetMethodInfo().Name is "TryShowAsync" or "TryNotifyAsync").ShouldBe(1);
        s.Operation.Received(1).Dispose();
    });

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Run(Func<Scope, Task> body)
    {
        var s = new Scope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(s); } catch (Exception ex) { primary = ex; }
        finally
        {
            foreach (var release in s.Release) release();
            foreach (var (work, allowed) in s.Tasks) try { await work; } catch (Exception ex) { if (allowed == null || !allowed.IsInstanceOfType(ex)) errors.Add(ex); }
            s.OperationToken.Dispose();
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Scope
    {
        public ITranslateWindowPresenter Presenter { get; } = Substitute.For<ITranslateWindowPresenter>();
        public ITranslateWindowOperation Operation { get; } = Substitute.For<ITranslateWindowOperation>();
        public CancellationTokenSource OperationToken { get; } = new();
        public ISelectionTextReader Reader { get; } = Substitute.For<ISelectionTextReader>();
        public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();
        public IDesktopContext Desktop { get; } = Substitute.For<IDesktopContext>();
        public AppSettings Current { get; set; } = new();
        public int SettingsReads;
        public TranslateWindowRequest? Shown { get; private set; }
        public ITranslateWindowOperation? ShownOperation { get; private set; }
        public RecordingLogger Log { get; } = new();
        public SelectionTranslateHandler Handler { get; }
        public ActionContext Context { get; } = new(ActionTrigger.Hotkey, new PixelPoint(12, 34), "ignored cached payload");
        public List<Action> Release { get; } = [];
        public List<(Task Work, Type? Allowed)> Tasks { get; } = [];
        public T Track<T>(T task, Type? allowed = null) where T : Task { Tasks.Add((task, allowed)); return task; }
        public Scope()
        {
            Operation.CancellationToken.Returns(OperationToken.Token);
            Presenter.TryBeginOperation(Arg.Any<CancellationToken>()).Returns(Operation);
            Presenter.TryShowAsync(Arg.Any<ITranslateWindowOperation>(), Arg.Any<TranslateWindowRequest>()).Returns(call =>
            { Shown = call.Arg<TranslateWindowRequest>(); ShownOperation = call.Arg<ITranslateWindowOperation>(); return Task.FromResult(true); });
            Settings.Current.Returns(_ => { Interlocked.Increment(ref SettingsReads); return Current; });
            Desktop.GetForegroundApp().Returns(new ForegroundAppInfo("external.exe", Environment.ProcessId + 1, "private title", 1));
            Handler = new(Presenter, Reader, Settings, Desktop, Log);
        }
    }
    private sealed class RecordingLogger : ILogger<SelectionTranslateHandler>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }
}
