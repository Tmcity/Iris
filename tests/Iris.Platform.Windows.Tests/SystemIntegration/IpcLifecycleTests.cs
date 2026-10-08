using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class IpcLifecycleTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public Task ClientCancellationJoinsNoncooperativeRead_AndFailedCleanupRetriesWithoutSending(bool deadline) => Run(async f =>
    {
        f.Factory.IsClient = true; using var caller = new CancellationTokenSource();
        var run = f.Client.RunAsync(new(IrisAction.InputTranslate), caller.Token); f.RunTask = run;
        await f.Factory.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) f.Time.Advance(TimeSpan.FromSeconds(10)); else caller.Cancel();
        var dispose = f.Client.DisposeAsync().AsTask(); f.FirstCleanup = dispose;
        f.Client.DisposeAsync().AsTask().ShouldBeSameAs(dispose);
        run.IsCompleted.ShouldBeFalse(); dispose.IsCompleted.ShouldBeFalse();
        f.Factory.ReleaseRead.TrySetResult();
        (await run).ShouldBe(IpcSendResult.Unconfirmed);
        await f.Factory.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispose.IsCompleted.ShouldBeFalse(); f.Factory.Disposed.ShouldBeFalse();
        f.Factory.ReleaseDispose.TrySetResult();
        await Should.ThrowAsync<IrisException>(() => dispose);
        f.Factory.FailDispose = false; await f.Client.DisposeAsync();
        f.Factory.Creates.ShouldBe(1); f.Factory.Writes.ShouldBe(1); f.Factory.Disposed.ShouldBeTrue();
    });

    [Fact]
    public Task ServerStopSharesActualWork_ThenRetriesOriginalFailedStream() => Run(async f =>
    {
        int accepted = 0; f.Server.Start(_ => { accepted++; return IpcReply.Accepted; });
        await f.Factory.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Server.StopAsync(); f.FirstCleanup = stop;
        f.Server.StopAsync().ShouldBeSameAs(stop); stop.IsCompleted.ShouldBeFalse();
        f.Factory.ReleaseRead.TrySetResult();
        await f.Factory.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.IsCompleted.ShouldBeFalse(); accepted.ShouldBe(0);
        f.Factory.ReleaseDispose.TrySetResult(); await Should.ThrowAsync<IrisException>(() => stop);
        f.Factory.FailDispose = false; await f.Server.StopAsync();
        f.Factory.Creates.ShouldBe(1); f.Factory.Disposed.ShouldBeTrue();
        f.Server.Start(_ => throw new InvalidOperationException("must remain sealed"));
        f.Factory.Creates.ShouldBe(1);
    });

    private static async Task Run(Func<Scope, Task> body)
    {
        var f = new Scope(); Exception? primary = null; var errors = new List<Exception>();
        try { await body(f); } catch (Exception ex) { primary = ex; }
        finally
        {
            f.Factory.ReleaseRead.TrySetResult(); f.Factory.ReleaseDispose.TrySetResult(); f.Factory.FailDispose = false;
            if (f.RunTask != null) try { await f.RunTask; } catch (Exception ex) { errors.Add(ex); }
            if (f.FirstCleanup != null) try { await f.FirstCleanup; } catch (IrisException) { /* Expected injected first cleanup failure. */ } catch (Exception ex) { errors.Add(ex); }
            try { await f.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) errors.Insert(0, primary);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Scope : IAsyncDisposable
    {
        internal readonly FakeTimeProvider Time = new(); internal readonly Factory Factory = new();
        internal readonly NamedPipeIpcClient Client; internal readonly NamedPipeIpcServer Server;
        internal Task? RunTask, FirstCleanup;
        internal Scope()
        {
            var identity = new IpcIdentity("Iris.Tests.Pipe." + Guid.NewGuid().ToString("N"), "unused");
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
    private sealed class Factory : IpcPipeFactory
    {
        internal readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Creates, Writes; internal bool IsClient, FailDispose = true, Disposed;
        internal override IpcPipe CreateClient(IpcIdentity identity) { Creates++; return new Pipe(this); }
        internal override IpcPipe CreateServer(IpcIdentity identity) { Creates++; return new Pipe(this); }
        private sealed class Pipe(Factory owner) : IpcPipe(new NamedPipeClientStream("Iris.Tests.Unconnected." + Guid.NewGuid().ToString("N")))
        {
            private readonly Stream _wire = new Wire(owner);
            internal override Stream Stream => _wire;
            internal override Task AcceptAsync(CancellationToken token) => Task.CompletedTask;
            internal override Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
            internal override void VerifyPeer(bool server) { }
            internal override void Disconnect() { }
            public override async ValueTask DisposeAsync()
            {
                owner.DisposeEntered.TrySetResult(); await owner.ReleaseDispose.Task;
                if (owner.FailDispose) throw new IOException("synthetic-dispose");
                await base.DisposeAsync(); owner.Disposed = true;
            }
        }
        private sealed class Wire(Factory owner) : Stream
        {
            private bool _read;
            public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.ReadEntered.TrySetResult(); await owner.ReleaseRead.Task;
                if (_read) return 0; _read = true;
                byte[] bytes = owner.IsClient ? "accepted\n"u8.ToArray() : IpcProtocol.Encode(new(IrisAction.Exit));
                bytes.CopyTo(buffer); return bytes.Length;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { owner.Writes++; return ValueTask.CompletedTask; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
