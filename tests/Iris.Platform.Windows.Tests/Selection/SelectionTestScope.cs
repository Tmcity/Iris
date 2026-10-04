using System.Runtime.ExceptionServices;
using System.Text;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Iris.Platform.Windows.Clipboard;
using Iris.Platform.Windows.Selection;
using Iris.Platform.Windows.Tests.Clipboard.TestSupport;
using NSubstitute;
using Shouldly;

namespace Iris.Platform.Windows.Tests.Selection;

internal sealed class SelectionTestScope
{
    public FakeClipboardSnapshotNative Native { get; } = new();
    public FakeNativeKeyboardInputAdapter Keys { get; } = new();
    public FakeCopySafetyNative Focus { get; } = new();
    public SelectionTimeProvider Time { get; } = new();
    public IDesktopContext Desktop { get; } = Substitute.For<IDesktopContext>();
    public ForegroundAppInfo? Source { get; set; } = new("editor.exe", 5000, "private title", 0x1234);
    public Action? OnSourceQuery { get; set; }
    public FakeUiaProcessRunner Runner { get; } = new();
    public UiaSelectionStatus UiaStatus { get; set; } = UiaSelectionStatus.Unsupported;
    public string UiaText { get; set; } = "uia text";
    public ClipboardSnapshotService Clipboard { get; }
    public KeyboardSimulator Keyboard { get; }
    public UiaSelectionReader Uia { get; }
    public CompositeSelectionReader Reader { get; }
    public List<(Task Task, Type? Allowed)> Tasks { get; } = [];
    public List<ManualResetEventSlim> Gates { get; } = [];
    public List<Action> Release { get; } = [];
    public List<ClipboardSnapshot> Snapshots { get; } = [];
    public bool Joined { get; private set; }
    public Action? AfterCleanup { get; set; }

    public SelectionTestScope()
    {
        Desktop.GetForegroundApp().Returns(_ => { OnSourceQuery?.Invoke(); return Source; });
        Clipboard = new(Native);
        Keyboard = new(Keys, Time);
        Uia = new(Runner, new FakeTargetWindowValidator(), Time);
        Reader = new(Uia, Clipboard, Keyboard, Desktop, new CopySafetyPolicy(Focus), Time);
        Runner.StartAsyncFunc = _ =>
        {
            var session = new FakeUiaProcessSession();
            session.EnqueueReady();
            var response = Task.Run(async () =>
            {
                var line = await UiaProtocolFrameReader.ReadLineWithLimitAsync(session.TestStdinReader, UiaProtocolConstants.MaxFrameBytes, CancellationToken.None);
                if (line == null) return;
                var request = UiaProtocolCodec.DeserializeRequest(line)!;
                session.EnqueueReply(new UiaReplyFrame
                {
                    ProtocolVersion = 1, Kind = "Reply", RequestId = request.RequestId,
                    Status = UiaStatus, Text = UiaStatus == UiaSelectionStatus.Success ? UiaText : null
                });
                session.SignalExit();
            }, CancellationToken.None);
            Track(response);
            return Task.FromResult<IUiaProcessSession>(session);
        };
    }

    public static SelectionReadOptions Options(bool restore = true, params SelectionReadMethod[] order) =>
        new(order.Length == 0 ? [SelectionReadMethod.Clipboard] : order, restore, 100, []);
    public T Track<T>(T task, Type? allowed = null) where T : Task { lock (Tasks) Tasks.Add((task, allowed)); return task; }
    public void Seed(string text = "old text") => Native.Add(13, Encoding.Unicode.GetBytes(text + '\0'));
    public string ClipboardText() => Encoding.Unicode.GetString(Native.Memory[Native.Formats.Single(f => f.Format == 13).Handle].Bytes).TrimEnd('\0');
    public int Copies => Keys.RecordedInputs.Count(i => (int)i.Input.Anonymous.ki.wVk == 0x43 && i.Input.Anonymous.ki.dwFlags == 0);
    public void CopyOnSend(string text = " selected text ", uint pid = 5000, nint owner = 0x1234)
    {
        Keys.CustomSendInputHandler = (_, inputs, _) =>
        {
            if ((int)inputs[0].Anonymous.ki.wVk == 0x43 && inputs[0].Anonymous.ki.dwFlags == 0) ExternalCopy(text, pid, owner);
            return 1;
        };
    }
    public void ExternalCopy(string text, uint pid = 5000, nint owner = 0x1234)
    {
        Native.Formats.Clear();
        Native.Add(13, Encoding.Unicode.GetBytes(text + '\0'));
        Native.Version = new(unchecked(Native.Version.Sequence + 1), owner, pid);
    }
    public (Task Entered, ManualResetEventSlim Release) NativeGate(string api)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        Gates.Add(release);
        Native.Before = call => { if (call == api) { entered.TrySetResult(); release.Wait(); } };
        return (entered.Task, release);
    }
    public async Task Tick(int milliseconds, int? scheduled = null)
    {
        await Time.WaitForDelayAsync(scheduled ?? milliseconds);
        Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
    }
    public static async Task Run(Func<SelectionTestScope, Task> body, SelectionTestScope? existing = null)
    {
        var s = existing ?? new SelectionTestScope();
        Exception? primary = null;
        var errors = new List<Exception>();
        try { await body(s); }
        catch (Exception ex) { primary = ex; }
        finally
        {
            s.Native.Before = null; s.Native.After = null; s.Native.Failures.Clear();
            s.Keys.CustomSendInputHandler = null; s.Keys.CustomGetForegroundWindowHandler = null;
            s.Keys.SendInputReleaseTcs?.TrySetResult(); s.Focus.Before = null; s.OnSourceQuery = null;
            foreach (var release in s.Release) try { release(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var gate in s.Gates) gate.Set();
            try { await s.Reader.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            foreach (var (task, allowed) in s.Tasks.ToArray())
                try { await task; } catch (Exception ex) { if (allowed == null || !allowed.IsInstanceOfType(ex)) errors.Add(ex); }
            try { await s.Reader.RetryCleanupAsync(); await s.Reader.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await s.Keyboard.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await s.Uia.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await s.Clipboard.DisposeAsync(); s.Joined = true; } catch (Exception ex) { errors.Add(ex); }
            foreach (var snapshot in s.Snapshots) snapshot.Dispose();
            foreach (var gate in s.Gates) gate.Dispose();
            s.Time.Dispose();
            try { s.Native.OwnershipViolations.ShouldBe(0); s.Native.IsOpen.ShouldBeFalse(); s.Native.WindowAlive.ShouldBeFalse(); }
            catch (Exception ex) { errors.Add(ex); }
            try { s.AfterCleanup?.Invoke(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
