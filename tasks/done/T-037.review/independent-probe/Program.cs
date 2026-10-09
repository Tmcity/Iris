using System.IO;
using System.IO.Pipes;
using System.Text;
using Iris.App.Hosting;
using Iris.Core.Actions;
using Iris.Core.Serialization;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);

    public static async Task<int> Main()
    {
        int failed = 0;
        var scenarios = new (string, Func<Task>)[]
        {
            ("real local pipe and compact Unicode frame", RealWire),
            ("normal ACK survives delayed native client read", DelayedAck),
            ("canceled noncooperative write starts no later read", LateWrite),
            ("stopped noncooperative accept starts no later read", LateAccept),
            ("canceled partial read starts no next read", LateFragment),
            ("unstarted coordinator disposes through real Host synchronously", UnstartedHost)
        };
        foreach (var (name, run) in scenarios)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " [" + ex.GetType().Name + "]"); }
        }
        Console.WriteLine($"Independent IPC scenarios: {scenarios.Length}; failed: {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }

    private static async Task Owned(Func<Task> body, Action open, params Func<Task>[] cleanups)
    {
        List<Exception> errors = [];
        try { await body(); } catch (Exception ex) { errors.Add(ex); }
        finally
        {
            open();
            foreach (var cleanup in cleanups)
                try { await cleanup(); } catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    private static IpcIdentity Identity() => new("Iris.Review.Pipe." + Guid.NewGuid().ToString("N"), "unused-review-mutex");

    private static async Task RealWire()
    {
        var identity = Identity(); var factory = new IpcPipeFactory();
        var server = new NamedPipeIpcServer(identity, factory, TimeProvider.System, NullLogger<NamedPipeIpcServer>.Instance);
        var client = new NamedPipeIpcClient(identity, factory, TimeProvider.System);
        IpcCommand? received = null; int accepts = 0;
        var command = new IpcCommand(IrisAction.TranslateText, "  synthetic 中文 😀\r\n\"text\" --action Exit  ");
        await Owned(async () =>
        {
            var bytes = IpcProtocol.Encode(command);
            Check(bytes.Count(b => b == 10) == 1 && IrisJson.Options.WriteIndented, "Global JSON or line framing changed");
            Check(!command.ToString().Contains("synthetic", StringComparison.Ordinal), "Payload leaked in ToString");
            server.Start(value => { received = value; accepts++; return IpcReply.Accepted; });
            var result = await client.RunAsync(command);
            Check(result == IpcSendResult.Accepted && received == command && accepts == 1, "Wire command did not arrive exactly once");
        }, () => { }, async () => await client.DisposeAsync(), () => server.StopAsync(), async () => await server.DisposeAsync());
    }

    private static async Task LateWrite()
    {
        var wire = new ProbeStream { HoldWrite = true, Reply = "accepted\n"u8.ToArray() };
        var pipe = new ProbePipe(wire); var factory = new Factory(pipe);
        var client = new NamedPipeIpcClient(Identity(), factory, TimeProvider.System);
        Task<IpcSendResult>? run = null; Task? cleanup = null;
        await Owned(async () =>
        {
            run = client.RunAsync(new(IrisAction.InputTranslate));
            await wire.Entered.Task.WaitAsync(ObservationLimit);
            cleanup = client.DisposeAsync().AsTask();
            await wire.Canceled.Task.WaitAsync(ObservationLimit);
            Check(!run.IsCompleted && !cleanup.IsCompleted, "Cancellation abandoned an active write");
            wire.Release.TrySetResult();
            var outcome = await run; await cleanup;
            Console.WriteLine($"LATE_WRITE reads={wire.Reads}; writes={wire.Writes}; creates={factory.Clients}; disposed={pipe.Disposed}; result={outcome}");
            Check(wire.Reads == 0 && wire.Writes == 1 && factory.Clients == 1 && pipe.Disposed, "New read started after canceled write");
            Check(outcome == IpcSendResult.Unconfirmed, "Canceled send claimed confirmed acceptance");
        }, () => wire.Release.TrySetResult(),
        async () => { if (run != null) await run; },
        async () => { if (cleanup != null) await cleanup; },
        async () => await client.DisposeAsync());
    }

    private static async Task DelayedAck()
    {
        var identity = Identity(); var factory = new AckFactory();
        var server = new NamedPipeIpcServer(identity, factory, TimeProvider.System, NullLogger<NamedPipeIpcServer>.Instance);
        var client = new NamedPipeIpcClient(identity, factory, TimeProvider.System);
        Task<IpcSendResult>? run = null; int accepts = 0;
        await Owned(async () =>
        {
            server.Start(_ => { accepts++; return IpcReply.Accepted; });
            run = client.RunAsync(new(IrisAction.InputTranslate));
            await Task.WhenAll(factory.AckWritten.Task, factory.ClientReadEntered.Task, factory.EndStage.Task).WaitAsync(ObservationLimit);
            // Actual ACK write and the following transport boundary are observed, with
            // the native client read still held. No delay is used to guess progress.
            int beforeRead = factory.Disconnects;
            factory.ReleaseClientRead.TrySetResult();
            var outcome = await run;
            Console.WriteLine($"ACK_BUFFER disconnectedBeforeRead={beforeRead}; accepted={accepts}; result={outcome}");
            Check(outcome == IpcSendResult.Accepted && accepts == 1, "Normal unread ACK was discarded by connection teardown");
        }, () => factory.ReleaseClientRead.TrySetResult(),
        async () => { if (run != null) await run; }, async () => await client.DisposeAsync(), () => server.StopAsync());
    }

    private static async Task LateAccept()
    {
        var wire = new ProbeStream { Reply = IpcProtocol.Encode(new(IrisAction.InputTranslate)) };
        var pipe = new ProbePipe(wire) { HoldAccept = true }; var factory = new Factory(pipe);
        var server = new NamedPipeIpcServer(Identity(), factory, TimeProvider.System, NullLogger<NamedPipeIpcServer>.Instance);
        Task? stop = null; int accepts = 0;
        await Owned(async () =>
        {
            server.Start(_ => { accepts++; return IpcReply.Accepted; });
            await pipe.Entered.Task.WaitAsync(ObservationLimit);
            stop = server.StopAsync(); await pipe.Canceled.Task.WaitAsync(ObservationLimit);
            Check(!stop.IsCompleted, "Stop abandoned an active accept");
            pipe.Release.TrySetResult(); await stop;
            Console.WriteLine($"LATE_ACCEPT reads={wire.Reads}; dispatches={accepts}; disposed={pipe.Disposed}");
            Check(wire.Reads == 0 && accepts == 0 && pipe.Disposed, "New connection work started after stop");
        }, () => pipe.Release.TrySetResult(),
        async () => { if (stop != null) await stop; }, () => server.StopAsync());
    }

    private static async Task LateFragment()
    {
        using var caller = new CancellationTokenSource();
        var wire = new ProbeStream { HoldFirstRead = true, Reply = "{"u8.ToArray() };
        Task<byte[]>? read = null; Exception? failure = null;
        await Owned(async () =>
        {
            read = IpcProtocol.ReadLineAsync(wire, 64, caller.Token);
            await wire.Entered.Task.WaitAsync(ObservationLimit);
            caller.Cancel(); Check(!read.IsCompleted, "Cancellation abandoned the old read");
            wire.Release.TrySetResult();
            try { await read; } catch (Exception ex) { failure = ex; }
            Console.WriteLine($"LATE_FRAGMENT reads={wire.Reads}; exception={failure?.GetType().Name}");
            Check(wire.Reads == 1 && failure is OperationCanceledException, "Codec continued reading after cancellation");
        }, () => wire.Release.TrySetResult(), async () =>
        {
            if (read != null) try { await read; } catch (OperationCanceledException) { }
            catch (Exception) when (failure != null) { }
            await wire.DisposeAsync();
        });
    }

    private static async Task UnstartedHost()
    {
        ThreadPool.GetMaxThreads(out int workers, out _);
        Check(workers == 1, "This isolated probe requires its own one-worker runtime configuration");
        var factory = new Factory(new ProbePipe(new ProbeStream()));
        var coordinator = new IpcCommandCoordinator(Identity(), factory, TimeProvider.System,
            new Dispatcher(), NullLoggerFactory.Instance);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(_ => coordinator);
        var host = builder.Build();
        host.Services.GetRequiredService<IpcCommandCoordinator>();
        Exception? failure = null; bool transportCompleted = false;
        await Owned(async () =>
        {
            // Running on the sole worker makes an unnecessary ForceYield deterministic.
            // This process-local runtime setting neither changes the product nor the machine.
            await Task.Run(() =>
            {
                try { host.Dispose(); transportCompleted = coordinator.StopTransportAsync().IsCompletedSuccessfully; }
                catch (Exception ex) { failure = ex; }
            });
            Console.WriteLine($"UNSTARTED_HOST servers={factory.Servers}; transportComplete={transportCompleted}; exception={failure?.GetType().Name ?? "none"}");
            Check(failure == null && transportCompleted && factory.Servers == 0, "Unused IPC required asynchronous scheduling to dispose");
        }, () => { }, () => coordinator.StopTransportAsync(), () => coordinator.WaitForDispatchesAsync(),
        () => { coordinator.Dispose(); host.Dispose(); return Task.CompletedTask; },
        async () => await factory.Pipe.DisposeAsync());
    }

    private sealed class Dispatcher : IActionDispatcher
    {
        public Task DispatchAsync(IrisAction action, ActionContext context, CancellationToken token = default)
            => throw new InvalidOperationException("An unstarted host must not dispatch");
    }

    private sealed class Factory(ProbePipe pipe) : IpcPipeFactory
    {
        internal readonly ProbePipe Pipe = pipe;
        internal int Clients, Servers;
        internal override IpcPipe CreateClient(IpcIdentity identity) { Clients++; return Pipe; }
        internal override IpcPipe CreateServer(IpcIdentity identity) { Servers++; return Pipe; }
    }

    private sealed class ProbePipe(ProbeStream wire)
        : IpcPipe(new NamedPipeClientStream(".", "Iris.Review.Unconnected." + Guid.NewGuid().ToString("N"), PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldAccept, Disposed;
        internal override Stream Stream => wire;
        internal override Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        internal override async Task AcceptAsync(CancellationToken token)
        {
            using var registration = token.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            if (HoldAccept) await Release.Task; // Deliberately ignores cancellation until released.
        }
        internal override void VerifyPeer(bool server) { }
        internal override void Disconnect() { }
        public override async ValueTask DisposeAsync()
        { await base.DisposeAsync(); await wire.DisposeAsync(); Disposed = true; }
    }

    private sealed class ProbeStream : Stream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldWrite, HoldFirstRead;
        internal int Reads, Writes;
        internal byte[] Reply = [];
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Writes++; using var registration = token.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult(); if (HoldWrite) await Release.Task;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Reads++; using var registration = token.Register(() => Canceled.TrySetResult());
            if (Reads > 1) return 0;
            Entered.TrySetResult(); if (HoldFirstRead) await Release.Task;
            int count = Math.Min(Reply.Length, buffer.Length); Reply.AsMemory(0, count).CopyTo(buffer); return count;
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private sealed class AckFactory : IpcPipeFactory
    {
        internal readonly TaskCompletionSource AckWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ClientReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseClientRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource EndStage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disconnects;
        internal void Ending() { if (AckWritten.Task.IsCompleted) EndStage.TrySetResult(); }
        internal override IpcPipe CreateServer(IpcIdentity identity) => new AckPipe(base.CreateServer(identity), this, true);
        internal override IpcPipe CreateClient(IpcIdentity identity) => new AckPipe(base.CreateClient(identity), this, false);
    }

    private sealed class AckPipe(IpcPipe inner, AckFactory state, bool server) : IpcPipe((PipeStream)inner.Stream)
    {
        private readonly Stream _stream = new AckStream(inner.Stream, state, server);
        internal override Stream Stream => _stream;
        internal override Task AcceptAsync(CancellationToken token)
        { state.Ending(); return base.AcceptAsync(token); }
        internal override void Disconnect()
        { state.Ending(); base.Disconnect(); Interlocked.Increment(ref state.Disconnects); }
        public override async ValueTask DisposeAsync()
        { if (server) state.Ending(); await base.DisposeAsync(); }
    }

    private sealed class AckStream(Stream inner, AckFactory state, bool server) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (server) state.Ending();
            else
            {
                state.ClientReadEntered.TrySetResult();
                await state.ReleaseClientRead.Task;
            }
            return await inner.ReadAsync(buffer, token);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await inner.WriteAsync(buffer, token);
            if (server) state.AckWritten.TrySetResult();
        }
        public override Task FlushAsync(CancellationToken token)
        { if (server) state.Ending(); return inner.FlushAsync(token); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
