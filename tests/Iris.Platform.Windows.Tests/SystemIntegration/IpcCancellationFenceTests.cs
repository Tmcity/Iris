using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using Iris.Core.Actions;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class IpcCancellationFenceTests
{
    [Theory]
    [InlineData("write", false)][InlineData("write", true)]
    [InlineData("read", false)][InlineData("read", true)]
    public Task ClientDoesNotStartAnotherIoAfterCanceledNoncooperativeOperation(string phase, bool deadline) => Run(phase, async f =>
    {
        using var caller = new CancellationTokenSource();
        var run = f.Client.RunAsync(new(IrisAction.InputTranslate), caller.Token); f.Work = run;
        await f.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) f.Time.Advance(TimeSpan.FromSeconds(10)); else caller.Cancel();
        run.IsCompleted.ShouldBeFalse(); f.Factory.Disposed.ShouldBeFalse();
        f.Factory.Release.TrySetResult();
        (await run).ShouldBe(IpcSendResult.Unconfirmed);
        f.Factory.Reads.ShouldBe(phase == "write" ? 0 : 1);
        await f.Client.DisposeAsync(); f.Factory.Writes.ShouldBe(1); f.Factory.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task ServerLateAcceptAfterStopDoesNotStartReadOrDispatch() => Run("accept", async f =>
    {
        int dispatches = 0;
        f.Server.Start(_ => { dispatches++; return IpcReply.Accepted; });
        await f.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Server.StopAsync(); f.Work = stop;
        stop.IsCompleted.ShouldBeFalse(); f.Factory.Disposed.ShouldBeFalse();
        f.Factory.Release.TrySetResult(); await stop;
        f.Factory.Reads.ShouldBe(0); dispatches.ShouldBe(0); f.Factory.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task CodecDoesNotReadAnotherFragmentAfterCancellation() => Run("read", async f =>
    {
        using var caller = new CancellationTokenSource();
        var pipe = f.Factory.CreateClient(new("unused", "unused"));
        try
        {
            var reading = IpcProtocol.ReadLineAsync(pipe.Stream, 64, caller.Token); f.Work = reading;
            await f.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel(); reading.IsCompleted.ShouldBeFalse(); f.Factory.Release.TrySetResult();
            Exception? failure = null;
            try { await reading; } catch (Exception ex) { failure = ex; }
            f.Factory.Reads.ShouldBe(1); failure.ShouldBeAssignableTo<OperationCanceledException>();
        }
        finally { f.Factory.Release.TrySetResult(); await pipe.DisposeAsync(); }
    });

    [Fact]
    public Task ServerLateAcknowledgementWriteAfterStopDoesNotStartPeerCloseRead() => Run("reply", async f =>
    {
        int dispatches = 0; f.Server.Start(_ => { dispatches++; return IpcReply.Accepted; });
        await f.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatches.ShouldBe(1); f.Factory.Reads.ShouldBe(1);
        var stop = f.Server.StopAsync(); f.Work = stop; stop.IsCompleted.ShouldBeFalse();
        f.Factory.Release.TrySetResult(); await stop;
        f.Factory.Reads.ShouldBe(1); dispatches.ShouldBe(1); f.Factory.Disposed.ShouldBeTrue();
    });

    private static async Task Run(string phase, Func<Scope, Task> body)
    {
        var f = new Scope(phase); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            f.Factory.Release.TrySetResult();
            if (f.Work != null) try { await f.Work; } catch (OperationCanceledException) { } catch (Iris.Core.Errors.IrisException) when (phase == "read") { } catch (Exception ex) { errors.Add(ex); }
            try { await f.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Scope : IAsyncDisposable
    {
        internal readonly FakeTimeProvider Time = new(); internal readonly Factory Factory;
        internal readonly NamedPipeIpcClient Client; internal readonly NamedPipeIpcServer Server;
        internal Task? Work;
        internal Scope(string phase)
        {
            Factory = new(phase); var identity = new IpcIdentity("Iris.Tests.Pipe." + Guid.NewGuid().ToString("N"), "unused");
            Client = new(identity, Factory, Time); Server = new(identity, Factory, Time, NullLogger<NamedPipeIpcServer>.Instance);
        }
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            try { await Client.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await Server.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count != 0) throw new AggregateException(errors);
        }
    }
    private sealed class Factory(string phase) : IpcPipeFactory
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads, Writes; internal bool Disposed;
        internal async Task Pause() { Entered.TrySetResult(); await Release.Task; }
        internal override IpcPipe CreateClient(IpcIdentity identity) => new Pipe(this, phase);
        internal override IpcPipe CreateServer(IpcIdentity identity) => new Pipe(this, phase);
        private sealed class Pipe(Factory owner, string phase) : IpcPipe(new NamedPipeClientStream("Iris.Tests.Unconnected." + Guid.NewGuid().ToString("N")))
        {
            private readonly Stream _stream = new Wire(owner, phase);
            internal override Stream Stream => _stream;
            internal override Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
            internal override Task AcceptAsync(CancellationToken token) => phase == "accept" ? owner.Pause() : Task.CompletedTask;
            internal override void VerifyPeer(bool server) { }
            internal override void Disconnect() { }
            public override async ValueTask DisposeAsync() { await base.DisposeAsync(); owner.Disposed = true; }
        }
        private sealed class Wire(Factory owner, string phase) : Stream
        {
            public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.Reads++;
                if (phase == "read")
                {
                    if (owner.Reads > 1) return 0;
                    await owner.Pause(); buffer.Span[0] = (byte)'a'; return 1;
                }
                byte[] bytes = phase is "accept" or "reply" ? IpcProtocol.Encode(new(IrisAction.Exit)) : "accepted\n"u8.ToArray();
                bytes.CopyTo(buffer); return bytes.Length;
            }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            { owner.Writes++; if (phase is "write" or "reply") await owner.Pause(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
