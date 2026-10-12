using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using Iris.Core.Actions;
using Iris.Core.Errors;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Iris.Platform.Windows.Tests.SystemIntegration;

public sealed class NamedPipeIpcTests
{
    [Fact]
    public Task RealPipes_SequentialClients_UnicodeAndNativeSessionVerification() => Run(async f =>
    {
        f.Server.Start(f.Accept);
        foreach (string text in new[] { "first", " 中文 😀\r\n\"quoted\" ", "last" })
        {
            var command = new IpcCommand(IrisAction.TranslateText, text);
            (await f.Send(command)).ShouldBe(IpcSendResult.Accepted);
            f.Accepted.Last().ShouldBe(command);
        }
        f.Factory.Clients.ShouldBe(3); f.Factory.Servers.ShouldBe(1);
        await f.Server.StopAsync();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        (await f.Client().RunAsync(new(IrisAction.Exit), canceled.Token)).ShouldBe(IpcSendResult.Unavailable);
    });

    [Fact]
    public Task FirstPipeInstanceRejectsAnotherServerWithoutReplacingTheOwner() => Run(async f =>
    {
        f.Server.Start(f.Accept);
        await using var other = new NamedPipeIpcServer(f.Identity, f.Factory, f.Time, NullLogger<NamedPipeIpcServer>.Instance);
        Should.Throw<IrisException>(() => other.Start(f.Accept));
        (await f.Send(new(IrisAction.InputTranslate))).ShouldBe(IpcSendResult.Accepted);
        f.Accepted.Count.ShouldBe(1);
    });

    [Fact]
    public Task ClientSessionMismatchRejectsBeforeAnyPayloadWrite() => Run(async f =>
    {
        f.Factory.RejectClientSession = true; f.Server.Start(f.Accept);
        (await f.Send(new(IrisAction.TranslateText, "private"))).ShouldBe(IpcSendResult.Unavailable);
        f.Factory.ClientWrites.ShouldBe(0); f.Accepted.ShouldBeEmpty();
    });

    [Fact]
    public Task ServerSessionMismatchRejectsWithoutDispatching() => Run(async f =>
    {
        f.Factory.RejectServerSession = true; f.Server.Start(f.Accept);
        var result = await f.Send(new(IrisAction.Exit));
        result.ShouldBe(f.Factory.ClientWrites == 0 ? IpcSendResult.Unavailable : IpcSendResult.Unconfirmed);
        f.Accepted.ShouldBeEmpty();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task LostAcknowledgementOrPartialWriteNeverReconnectsOrRedispatches(bool partialWrite) => Run(async f =>
    {
        f.Factory.DropReply = !partialWrite; f.Factory.PartialWrite = partialWrite; f.Server.Start(f.Accept);
        var client = f.Client();
        (await client.RunAsync(new(IrisAction.TranslateText, "private"))).ShouldBe(IpcSendResult.Unconfirmed);
        await client.DisposeAsync();
        Should.Throw<IrisException>(() => client.RunAsync(new(IrisAction.Exit)));
        f.Factory.Clients.ShouldBe(1); f.Factory.ClientWrites.ShouldBe(1);
        f.Accepted.Count.ShouldBe(partialWrite ? 0 : 1);
    });

    [Fact]
    public Task SlowClientDeadlineFreesServerForNextClient() => Run(async f =>
    {
        f.Server.Start(f.Accept);
        await using var idle = new NamedPipeClientStream(".", f.Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await idle.ConnectAsync(TestContext.Current.CancellationToken);
        await f.Factory.ServerVerified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Time.Advance(TimeSpan.FromSeconds(10));
        (await f.Send(new(IrisAction.InputTranslate))).ShouldBe(IpcSendResult.Accepted);
        f.Accepted.Count.ShouldBe(1);
    });

    [Fact]
    public Task AdditionalFrameIsNeverDispatched() => Run(async f =>
    {
        f.Server.Start(f.Accept);
        await using var client = new NamedPipeClientStream(".", f.Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        var frame = IpcProtocol.Encode(new(IrisAction.InputTranslate));
        await client.WriteAsync(frame.Concat(IpcProtocol.Encode(new(IrisAction.Exit))).ToArray(), TestContext.Current.CancellationToken);
        var reply = await IpcProtocol.ReadLineAsync(client, 64, TestContext.Current.CancellationToken);
        IpcProtocol.DecodeReply(reply).ShouldBe(IpcSendResult.Accepted);
        f.Accepted.Count.ShouldBe(1); f.Accepted.Single().Action.ShouldBe(IrisAction.InputTranslate);
    });

    [Theory][InlineData("duplicate")][InlineData("numeric")][InlineData("utf8")]
    public Task RealWireRejectsFragmentedInvalidFramesAndKeepsServing(string kind) => Run(async f =>
    {
        f.Server.Start(f.Accept);
        await using (var client = new NamedPipeClientStream(".", f.Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            byte[] bytes = kind switch
            {
                "duplicate" => "{\"action\":\"Exit\",\"ACTION\":\"Exit\"}\n"u8.ToArray(),
                "numeric" => "{\"action\":12}\n"u8.ToArray(),
                _ => [0xC3, 0x28, (byte)'\n']
            };
            foreach (byte value in bytes) await client.WriteAsync(new byte[] { value }, TestContext.Current.CancellationToken);
            var reply = await IpcProtocol.ReadLineAsync(client, 64, TestContext.Current.CancellationToken);
            IpcProtocol.DecodeReply(reply).ShouldBe(IpcSendResult.Rejected); f.Accepted.ShouldBeEmpty();
        }
        (await f.Send(new(IrisAction.InputTranslate))).ShouldBe(IpcSendResult.Accepted);
        f.Accepted.Count.ShouldBe(1);
    });

    [Fact]
    public Task TruncatedWireDisconnectDoesNotDispatchAndNextConnectionStillWorks() => Run(async f =>
    {
        f.Server.Start(f.Accept);
        await using (var client = new NamedPipeClientStream(".", f.Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            await client.WriteAsync("{\"action\":\"Exit\"}"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        (await f.Send(new(IrisAction.InputTranslate))).ShouldBe(IpcSendResult.Accepted);
        f.Accepted.Count.ShouldBe(1); f.Accepted.Single().Action.ShouldBe(IrisAction.InputTranslate);
    });

    [Theory][InlineData("unknown\n")][InlineData("accepted extra\n")]
    public Task InvalidAcknowledgementRemainsUnconfirmedWithoutRetry(string reply) => Run(async f =>
    {
        f.Factory.ReplyOverride = System.Text.Encoding.ASCII.GetBytes(reply); f.Server.Start(f.Accept);
        (await f.Send(new(IrisAction.InputTranslate))).ShouldBe(IpcSendResult.Unconfirmed);
        f.Accepted.Count.ShouldBe(1); f.Factory.Clients.ShouldBe(1);
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task ActualAckWaitsForDelayedClientRead_AndDeadlineStillReleasesConnection(bool expire) => Run(async f =>
    {
        f.Factory.HoldClientRead = true; f.Server.Start(f.Accept);
        var client = f.Client(); var run = client.RunAsync(new(IrisAction.InputTranslate));
        await f.Factory.ClientReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Factory.ReplyWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Factory.PeerCloseWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.IsCompleted.ShouldBeFalse(); f.Factory.Disconnects.ShouldBe(0); f.Accepted.Count.ShouldBe(1);
        if (expire) f.Time.Advance(TimeSpan.FromSeconds(10));
        run.IsCompleted.ShouldBeFalse(); // The genuinely entered client read is still owned.
        f.Factory.ReleaseClientRead.TrySetResult();
        (await run).ShouldBe(expire ? IpcSendResult.Unconfirmed : IpcSendResult.Accepted);
        await client.DisposeAsync(); f.Factory.Clients.ShouldBe(1); f.Accepted.Count.ShouldBe(1);
        (await f.Send(new(IrisAction.OpenSettings))).ShouldBe(IpcSendResult.Accepted);
        f.Accepted.Count.ShouldBe(2);
    });

    private static async Task Run(Func<Fixture, Task> body)
    {
        var f = new Fixture(); Exception? error = null; var cleanup = new List<Exception>();
        try { await body(f); } catch (Exception ex) { error = ex; }
        finally
        {
            try { await f.DisposeAsync(); } catch (Exception ex) { cleanup.Add(ex); }
        }
        if (error != null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(error).Throw();
        if (error != null) cleanup.Insert(0, error);
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly IpcIdentity Identity = new("Iris.Tests.Pipe." + Guid.NewGuid().ToString("N"), "unused-test-mutex");
        internal readonly Factory Factory = new(); internal readonly FakeTimeProvider Time = new();
        internal readonly NamedPipeIpcServer Server;
        internal readonly ConcurrentQueue<IpcCommand> Accepted = new();
        internal readonly List<NamedPipeIpcClient> Clients = [];
        internal Fixture() => Server = new(Identity, Factory, Time, NullLogger<NamedPipeIpcServer>.Instance);
        internal IpcReply Accept(IpcCommand command) { Accepted.Enqueue(command); return IpcReply.Accepted; }
        internal NamedPipeIpcClient Client() { var client = new NamedPipeIpcClient(Identity, Factory, Time); Clients.Add(client); return client; }
        internal async Task<IpcSendResult> Send(IpcCommand command)
        {
            var client = Client();
            try { return await client.RunAsync(command); }
            finally { await client.DisposeAsync(); }
        }
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            Factory.ReleaseClientRead.TrySetResult();
            foreach (var client in Clients) try { await client.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            try { await Server.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count != 0) throw new AggregateException(errors);
        }
    }
    private sealed class Factory : IpcPipeFactory
    {
        internal int Servers, Clients, ClientWrites, Disconnects;
        internal bool RejectClientSession, RejectServerSession, DropReply, PartialWrite;
        internal bool HoldClientRead;
        internal byte[]? ReplyOverride;
        internal readonly TaskCompletionSource ServerVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ClientReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseClientRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReplyWritten = new(TaskCreationOptions.RunContinuationsAsynchronously), PeerCloseWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override IpcPipe CreateServer(IpcIdentity identity) { Servers++; return new Instrumented(base.CreateServer(identity), this, true); }
        internal override IpcPipe CreateClient(IpcIdentity identity) { Clients++; return new Instrumented(base.CreateClient(identity), this, false); }
    }
    private sealed class Instrumented(IpcPipe inner, Factory factory, bool server) : IpcPipe((PipeStream)inner.Stream)
    {
        private readonly Stream _stream = new Wire(inner.Stream, factory, server);
        internal override Stream Stream => _stream;
        internal override void VerifyPeer(bool server)
        {
            if (server ? factory.RejectServerSession : factory.RejectClientSession) throw IpcProtocol.Unavailable();
            base.VerifyPeer(server);
            if (server) factory.ServerVerified.TrySetResult();
        }
        internal override void Disconnect() { factory.Disconnects++; base.Disconnect(); }
    }
    private sealed class Wire(Stream inner, Factory factory, bool server) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!server && factory.HoldClientRead) { factory.ClientReadEntered.TrySetResult(); await factory.ReleaseClientRead.Task; }
            if (server && factory.ReplyWritten.Task.IsCompleted) factory.PeerCloseWaitEntered.TrySetResult();
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!server) factory.ClientWrites++;
            if (server && factory.DropReply) throw new IOException("synthetic-lost-ack");
            if (server && factory.ReplyOverride != null) { await inner.WriteAsync(factory.ReplyOverride, cancellationToken); return; }
            if (!server && factory.PartialWrite)
            {
                await inner.WriteAsync(buffer[..5], cancellationToken);
                throw new IOException("synthetic-partial-write");
            }
            await inner.WriteAsync(buffer, cancellationToken);
            if (server) factory.ReplyWritten.TrySetResult();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
