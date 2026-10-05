using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Selection;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

public sealed class ExpectedSelectionSourceTests
{
    [Theory]
    [InlineData("hwnd")]
    [InlineData("pid")]
    [InlineData("name")]
    [InlineData("whitespace")]
    public Task Mismatch_HasOnlySourceQueriesAndNoNewPlatformWork(string difference) => SelectionTestScope.Run(async s =>
    {
        var expected = s.Source!;
        expected = difference switch
        {
            "hwnd" => expected with { WindowHandle = 77 },
            "pid" => expected with { ProcessId = 7000 },
            "name" => expected with { ProcessName = "other.exe" },
            _ => expected with { ProcessName = " editor.exe " },
        };
        int queries = 0; s.OnSourceQuery = () => queries++;
        var result = await s.Reader.ReadAsync(SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard) with { ExpectedSource = expected }, CancellationToken.None);
        result.ShouldBeNull(); queries.ShouldBe(1);
        s.Runner.TotalStartCalls.ShouldBe(0); s.Keys.TotalNativeCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty(); s.Focus.Calls.ShouldBe(0);
    });

    [Theory]
    [InlineData("zero-window")]
    [InlineData("zero-pid")]
    [InlineData("negative-pid")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("C:\\private.exe")]
    [InlineData("folder/private.exe")]
    [InlineData("private:exe")]
    [InlineData("private\0exe")]
    public Task InvalidExpectedSource_IsSynchronousAndSafe(string input) => SelectionTestScope.Run(s =>
    {
        var expected = input switch
        {
            "zero-window" => s.Source! with { WindowHandle = 0 },
            "zero-pid" => s.Source! with { ProcessId = 0 },
            "negative-pid" => s.Source! with { ProcessId = -1 },
            _ => s.Source! with { ProcessName = input },
        };
        var error = Should.Throw<IrisException>(() => s.Reader.ReadAsync(SelectionTestScope.Options() with { ExpectedSource = expected }, CancellationToken.None));
        error.Code.ShouldBe(ErrorCode.InvalidArgument); error.ToString().ShouldNotContain("private");
        s.Runner.TotalStartCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty(); s.Keys.TotalNativeCalls.ShouldBe(0);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MatchingSource_IgnoresTitleAndCase_AndUsesActualServices(bool clipboard) => SelectionTestScope.Run(async s =>
    {
        s.UiaStatus = UiaSelectionStatus.Success; s.Seed(); s.CopyOnSend("from clipboard");
        var method = clipboard ? SelectionReadMethod.Clipboard : SelectionReadMethod.UIAutomation;
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(true, method) with
        { ExpectedSource = s.Source! with { ProcessName = "EDITOR.EXE", WindowTitle = "not current title" } }, CancellationToken.None));
        if (clipboard) await s.Tick(20);
        (await read)!.Method.ShouldBe(method);
        s.Runner.TotalStartCalls.ShouldBe(clipboard ? 0 : 1); s.Copies.ShouldBe(clipboard ? 1 : 0);
    });

    [Fact]
    public Task CapturedOptionsAndSource_StayFrozenWhileSourceQueryWaits() => SelectionTestScope.Run(async s =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(); s.Gates.Add(release);
        s.OnSourceQuery = () => { entered.TrySetResult(); release.Wait(); };
        s.UiaStatus = UiaSelectionStatus.Success;
        var order = new List<SelectionReadMethod> { SelectionReadMethod.UIAutomation, SelectionReadMethod.UIAutomation };
        var excluded = new List<string> { "other.exe" };
        var source = s.Source!;
        var options = new SelectionReadOptions(order, true, 100, excluded) { ExpectedSource = source };
        var frozen = SelectionReadOptionsSnapshot.Freeze(options);
        frozen.ExpectedSource!.WindowTitle.ShouldBeNull(); frozen.ExpectedSource.ShouldNotBeSameAs(source);
        var read = s.Track(s.Reader.ReadAsync(options, CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        order.Clear(); excluded.Add("editor.exe"); source = source with { WindowHandle = 17 };
        options = options with { ExpectedSource = source };
        release.Set();
        (await read)!.Method.ShouldBe(SelectionReadMethod.UIAutomation); s.Runner.TotalStartCalls.ShouldBe(1);
        frozen.Order.Count.ShouldBe(1); frozen.ClipboardUnsafeProcesses.ShouldBe(new[] { "other.exe" });
        options.ToString().ShouldBe("SelectionReadOptions");
    });

    [Fact]
    public Task ChangedSourceAfterAcceptedUia_NeverRetargetsFallback() => SelectionTestScope.Run(async s =>
    {
        var start = s.Runner.StartAsyncFunc!;
        s.Runner.StartAsyncFunc = token => { s.Source = s.Source! with { ProcessName = "other.exe" }; return start(token); };
        var options = SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation, SelectionReadMethod.Clipboard) with { ExpectedSource = s.Source };
        (await s.Reader.ReadAsync(options, CancellationToken.None)).ShouldBeNull();
        s.Runner.TotalStartCalls.ShouldBe(1); s.Keys.TotalNativeCalls.ShouldBe(0); s.Native.Calls.ShouldBeEmpty();
    });

    [Fact]
    public Task PendingRestore_IsRetriedBeforeMismatchedNewSource_AndFailureIsNotNull() => SelectionTestScope.Run(async s =>
    {
        s.Seed(); s.CopyOnSend();
        var read = s.Track(s.Reader.ReadAsync(SelectionTestScope.Options(), CancellationToken.None), typeof(IrisException));
        await s.Time.WaitForDelayAsync(20); s.Native.Failures["Set"] = -1; s.Time.Advance(TimeSpan.FromMilliseconds(20));
        await Should.ThrowAsync<IrisException>(() => read);
        var mismatch = SelectionTestScope.Options(true, SelectionReadMethod.UIAutomation) with { ExpectedSource = s.Source! with { WindowHandle = 77 } };
        await Should.ThrowAsync<IrisException>(() => s.Reader.ReadAsync(mismatch, CancellationToken.None));
        s.Native.Failures.Clear();
        var gate = s.NativeGate("Set");
        var next = s.Track(s.Reader.ReadAsync(mismatch, CancellationToken.None));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        next.IsCompleted.ShouldBeFalse(); s.Runner.TotalStartCalls.ShouldBe(0); s.Copies.ShouldBe(1);
        gate.Release.Set(); (await next).ShouldBeNull();
        s.ClipboardText().ShouldBe("old text"); s.Runner.TotalStartCalls.ShouldBe(0); s.Copies.ShouldBe(1);
    });

    [Fact]
    public void FourParameterConstructorAndDeconstruction_AreUnchangedAndToStringIsSafe()
    {
        var options = new SelectionReadOptions([SelectionReadMethod.UIAutomation], true, 100, ["private.exe"]);
        var (order, restore, timeout, excluded) = options;
        order.ShouldBeSameAs(options.Order); restore.ShouldBeTrue(); timeout.ShouldBe(100); excluded.ShouldBeSameAs(options.ClipboardUnsafeProcesses);
        options.ExpectedSource.ShouldBeNull();
        (options with { ExpectedSource = new("private.exe", 1, "private title", 1) }).ToString().ShouldBe("SelectionReadOptions");
    }
}
