using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using Iris.Core.Actions;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Windows.Win32;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class IpcEarlyDisconnectTests
{
    [Fact]
    public async Task NativeCloseBeforeAcceptRecordsWin32FailureAndResetBehavior()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string name = "Iris.Tests.EarlyNative." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(guard.Token);
        }
        var error = await Should.ThrowAsync<IOException>(() => server.WaitForConnectionAsync(guard.Token));
        Console.WriteLine($"EARLY_NATIVE_ACCEPT type={error.GetType().Name} hresult={error.HResult:X8} connected={server.IsConnected}");
        error.HResult.ShouldBe(unchecked((int)0x800700E8));
        Should.Throw<InvalidOperationException>(() => server.Disconnect());
        var originalHandle = server.SafePipeHandle;
        ((bool)PInvoke.DisconnectNamedPipe(originalHandle)).ShouldBeTrue();
        await using var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = server.WaitForConnectionAsync(guard.Token);
        try { await next.ConnectAsync(guard.Token); await accept; }
        finally { guard.Cancel(); await accept; }
        server.SafePipeHandle.ShouldBeSameAs(originalHandle);
        Console.WriteLine("EARLY_NATIVE_RESET sameHandle=True acceptedNext=True");
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealResetOrOtherAcceptFailureIsNotSwallowedAndSameServerCanFinishCleanup(bool failReset)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factory = new Factory { FailReset = failReset, FailOtherAccept = !failReset };
        var identity = new IpcIdentity("Iris.Tests.EarlyFailure." + Guid.NewGuid().ToString("N"), "unused");
        var server = new NamedPipeIpcServer(identity, factory, new FakeTimeProvider(), NullLogger<NamedPipeIpcServer>.Instance);
        int dispatched = 0;
        try
        {
            server.Start(_ => { dispatched++; return IpcReply.Accepted; });
            await factory.AcceptEntered.Task.WaitAsync(guard.Token);
            await using (var client = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                await client.ConnectAsync(guard.Token);
            factory.ReleaseAccept.TrySetResult();
            await factory.FirstAcceptReturned.Task.WaitAsync(guard.Token);
            await Should.ThrowAsync<Iris.Core.Errors.IrisException>(() => server.StopAsync());
            factory.ServerDisposed.ShouldBeFalse(); dispatched.ShouldBe(0); factory.Servers.ShouldBe(1);
            await using var contender = new NamedPipeIpcServer(identity, new IpcPipeFactory(), new FakeTimeProvider(), NullLogger<NamedPipeIpcServer>.Instance);
            Should.Throw<Iris.Core.Errors.IrisException>(() => contender.Start(_ => IpcReply.Accepted));
            await server.StopAsync(); factory.ServerDisposed.ShouldBeTrue();
        }
        finally { factory.ReleaseAccept.TrySetResult(); guard.Cancel(); await server.StopAsync(); }
    }

    [Fact]
    public async Task StopDuringHeldAcceptJoinsOldWorkAndRetriesOriginalDisposeFailure()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factory = new Factory { FailDisposeOnce = true };
        var identity = new IpcIdentity("Iris.Tests.EarlyStop." + Guid.NewGuid().ToString("N"), "unused");
        var server = new NamedPipeIpcServer(identity, factory, new FakeTimeProvider(), NullLogger<NamedPipeIpcServer>.Instance);
        Task? stopping = null;
        try
        {
            server.Start(_ => throw new InvalidOperationException("dispatch after sealing"));
            await factory.AcceptEntered.Task.WaitAsync(guard.Token);
            stopping = server.StopAsync(); stopping.IsCompleted.ShouldBeFalse();
            factory.ReleaseAccept.TrySetResult();
            await Should.ThrowAsync<Iris.Core.Errors.IrisException>(() => stopping);
            factory.ServerDisposed.ShouldBeFalse(); factory.DisposeAttempts.ShouldBe(1);
            await server.StopAsync(); factory.ServerDisposed.ShouldBeTrue();
            factory.DisposeAttempts.ShouldBe(2); factory.Servers.ShouldBe(1); factory.Resets.ShouldBe(0);
        }
        finally
        {
            factory.ReleaseAccept.TrySetResult(); guard.Cancel();
            try { if (stopping != null) await stopping; } catch (Iris.Core.Errors.IrisException) { }
            await server.StopAsync();
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task PeerClosesBeforeActualAcceptPreservesServerAndAcceptsNextCommand(bool rejectSession)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factory = new Factory { RejectClient = rejectSession };
        var identity = new IpcIdentity("Iris.Tests.Early." + Guid.NewGuid().ToString("N"), "unused");
        var time = new FakeTimeProvider();
        var server = new NamedPipeIpcServer(identity, factory, time, NullLogger<NamedPipeIpcServer>.Instance);
        int dispatched = 0;
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            server.Start(_ => { dispatched++; return IpcReply.Accepted; });
            await factory.AcceptEntered.Task.WaitAsync(guard.Token);
            if (rejectSession)
            {
                await using var client = new NamedPipeIpcClient(identity, factory, time);
                (await client.RunAsync(new(IrisAction.TranslateText, "synthetic"), guard.Token)).ShouldBe(IpcSendResult.Unavailable);
                factory.ClientWrites.ShouldBe(0);
            }
            else
            {
                await using var client = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(guard.Token);
                await client.WriteAsync("{\"action\":\"Exit\""u8.ToArray(), guard.Token);
            }
            dispatched.ShouldBe(0);
            factory.ReleaseAccept.TrySetResult();
            await Task.WhenAny(factory.EarlyReset.Task, factory.FirstAcceptReturned.Task).WaitAsync(guard.Token);
            Console.WriteLine($"EARLY_PRODUCT_ACCEPT reject={rejectSession} hresult={factory.AcceptError?.HResult:X8} servers={factory.Servers}");
            factory.RejectClient = false;
            await using var next = new NamedPipeIpcClient(identity, factory, time);
            (await next.RunAsync(new(IrisAction.InputTranslate), guard.Token)).ShouldBe(IpcSendResult.Accepted);
            dispatched.ShouldBe(1); factory.Servers.ShouldBe(1); factory.Resets.ShouldBe(1);
            await server.StopAsync();
            factory.ServerDisposed.ShouldBeTrue();
        }
        catch (Exception ex) { primary = ex; }
        finally
        {
            factory.ReleaseAccept.TrySetResult(); guard.Cancel(); server.StopAcceptance();
            try { await server.StopAsync(); }
            catch (Exception ex)
            {
                cleanup.Add(ex);
                // Observe the first error and finish cleanup on the same retained server instance.
                try { await server.StopAsync(); } catch (Exception retry) { cleanup.Add(retry); }
            }
        }
        if (primary != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary != null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }

    private sealed class Factory : IpcPipeFactory
    {
        internal readonly TaskCompletionSource AcceptEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseAccept = new(TaskCreationOptions.RunContinuationsAsynchronously), FirstAcceptReturned = new(TaskCreationOptions.RunContinuationsAsynchronously), EarlyReset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? AcceptError;
        internal int Servers, ClientWrites, Resets, DisposeAttempts;
        internal bool RejectClient, ServerDisposed, FailReset, FailOtherAccept, FailDisposeOnce;
        internal override IpcPipe CreateServer(IpcIdentity identity) { Servers++; return new ServerPipe(base.CreateServer(identity), this); }
        internal override IpcPipe CreateClient(IpcIdentity identity) => new ClientPipe(base.CreateClient(identity), this);
    }
    private sealed class ServerPipe(IpcPipe inner, Factory owner) : IpcPipe((PipeStream)inner.Stream)
    {
        private bool _first = true;
        internal override async Task AcceptAsync(CancellationToken token)
        {
            if (!_first) { await base.AcceptAsync(token); return; }
            _first = false; owner.AcceptEntered.TrySetResult();
            await owner.ReleaseAccept.Task;
            try
            {
                if (owner.FailOtherAccept) throw new IOException("synthetic other accept error", unchecked((int)0x80070005));
                await base.AcceptAsync(token);
            }
            catch (Exception ex) { owner.AcceptError = ex; throw; }
            finally { owner.FirstAcceptReturned.TrySetResult(); }
        }
        public override async ValueTask DisposeAsync()
        {
            owner.DisposeAttempts++;
            if (owner.FailDisposeOnce && owner.DisposeAttempts == 1) throw new IOException("synthetic dispose failure");
            await base.DisposeAsync(); owner.ServerDisposed = true;
        }
        internal override void DisconnectUnacceptedPeer()
        {
            if (owner.FailReset) throw new System.ComponentModel.Win32Exception(5);
            base.DisconnectUnacceptedPeer(); owner.Resets++; owner.EarlyReset.TrySetResult();
        }
    }
    private sealed class ClientPipe(IpcPipe inner, Factory owner) : IpcPipe((PipeStream)inner.Stream)
    {
        private readonly Stream _stream = new CountingStream(inner.Stream, owner);
        internal override Stream Stream => _stream;
        internal override void VerifyPeer(bool server) { base.VerifyPeer(server); if (owner.RejectClient) throw IpcProtocol.Unavailable(); }
    }
    private sealed class CountingStream(Stream inner, Factory owner) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { owner.ClientWrites++; return inner.WriteAsync(buffer, cancellationToken); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
