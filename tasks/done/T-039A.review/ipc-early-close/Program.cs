using System.IO.Pipes;
using System.Text.Json;
using Iris.Core.Actions;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging.Abstractions;

// Standalone review executable. Every pipe is random and owned by this process.
// Do not run concurrently with the developer's build/test session.
internal static class Program
{
    private static async Task<int> Main()
    {
        int failed = 0;
        foreach (bool truncated in new[] { false, true })
        {
            var result = await RunEarlyClose(truncated);
            Console.WriteLine(JsonSerializer.Serialize(result));
            if (!result.Healthy) failed++;
        }
        Console.WriteLine($"REVIEW_COMPLETED failures={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<Result> RunEarlyClose(bool truncated)
    {
        var result = new Result { Scenario = truncated ? "truncated-before-first-accept" : "empty-before-first-accept" };
        var identity = new IpcIdentity("Iris.Review.EarlyClose." + Guid.NewGuid().ToString("N"), "unused-review-mutex");
        var factory = new GatedFactory();
        var server = new NamedPipeIpcServer(identity, factory, TimeProvider.System, NullLogger<NamedPipeIpcServer>.Instance);
        var client = new NamedPipeIpcClient(identity, factory, TimeProvider.System);
        using var protection = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<IpcSendResult>? next = null;
        try
        {
            server.Start(command => { result.Dispatches++; result.Action = command.Action.ToString(); return IpcReply.Accepted; });
            await factory.AcceptEntered.Task.WaitAsync(protection.Token);
            await using (var early = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut,
                             PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await early.ConnectAsync(protection.Token);
                if (truncated)
                    await early.WriteAsync("{\"action\":\"Exit\"}"u8.ToArray(), protection.Token);
            }
            result.FirstClientClosedBeforeAccept = true;
            try
            {
                using var intruder = new NamedPipeServerStream(identity.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
                result.CompetingOwnerRejected = false;
            }
            catch (IOException) { result.CompetingOwnerRejected = true; }
            catch (UnauthorizedAccessException) { result.CompetingOwnerRejected = true; }

            factory.ReleaseAccept.TrySetResult();
            next = client.RunAsync(new IpcCommand(IrisAction.InputTranslate), protection.Token);
            result.NextResult = (await next).ToString();
            await client.DisposeAsync();
            await server.StopAsync();
            result.FirstStopSucceeded = true;
        }
        catch (Exception error)
        {
            result.Errors.Add(new("body", error.GetType().Name, error.HResult));
        }
        finally
        {
            // Release gates before joining any operation. The real cancellation token bounds
            // fixture liveness; it is not advanced fake time and does not replace IPC's deadline.
            factory.ReleaseAccept.TrySetResult();
            await protection.CancelAsync();
            if (next != null)
            {
                try { await next; }
                catch (Exception error) { result.Errors.Add(new("join-client", error.GetType().Name, error.HResult)); }
            }
            try { await client.DisposeAsync(); }
            catch (Exception error) { result.Errors.Add(new("client-cleanup", error.GetType().Name, error.HResult)); }
            try { await server.StopAsync(); result.FinalStopSucceeded = true; }
            catch (Exception error)
            {
                result.Errors.Add(new("server-cleanup", error.GetType().Name, error.HResult));
                try { await server.StopAsync(); result.FinalStopSucceeded = true; }
                catch (Exception retry) { result.Errors.Add(new("server-cleanup-retry", retry.GetType().Name, retry.HResult)); }
            }
            result.ServerCreates = factory.ServerCreates;
            result.AcceptCalls = factory.AcceptCalls;
            result.AcceptErrors = factory.Errors.ToArray();
        }
        return result;
    }

    private sealed class GatedFactory : IpcPipeFactory
    {
        internal readonly TaskCompletionSource AcceptEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<Failure> Errors = [];
        internal int ServerCreates, AcceptCalls;
        internal override IpcPipe CreateServer(IpcIdentity identity)
        {
            ServerCreates++;
            return new GatedPipe(base.CreateServer(identity), this);
        }
        private sealed class GatedPipe(IpcPipe inner, GatedFactory factory) : IpcPipe((PipeStream)inner.Stream)
        {
            internal override Stream Stream => inner.Stream;
            internal override void VerifyPeer(bool server) => inner.VerifyPeer(server);
            internal override void Disconnect() => inner.Disconnect();
            public override ValueTask DisposeAsync() => inner.DisposeAsync();
            internal override async Task AcceptAsync(CancellationToken token)
            {
                int attempt = Interlocked.Increment(ref factory.AcceptCalls);
                if (attempt == 1)
                {
                    factory.AcceptEntered.TrySetResult();
                    await factory.ReleaseAccept.Task;
                }
                try { await inner.AcceptAsync(token); }
                catch (Exception error)
                {
                    factory.Errors.Add(new("accept-" + attempt, error.GetType().Name, error.HResult));
                    throw;
                }
            }
        }
    }

    private sealed record Failure(string Stage, string Type, int HResult);
    private sealed class Result
    {
        public string Scenario { get; set; } = "";
        public bool FirstClientClosedBeforeAccept { get; set; }
        public bool CompetingOwnerRejected { get; set; }
        public int Dispatches { get; set; }
        public string? Action { get; set; }
        public string? NextResult { get; set; }
        public bool FirstStopSucceeded { get; set; }
        public bool FinalStopSucceeded { get; set; }
        public int ServerCreates { get; set; }
        public int AcceptCalls { get; set; }
        public Failure[] AcceptErrors { get; set; } = [];
        public List<Failure> Errors { get; } = [];
        public bool Healthy => FirstClientClosedBeforeAccept && CompetingOwnerRejected &&
            ServerCreates == 1 && Dispatches == 1 && Action == "InputTranslate" && NextResult == "Accepted" &&
            FirstStopSucceeded && FinalStopSucceeded && Errors.Count == 0;
    }
}
