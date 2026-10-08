using Iris.Core.Actions;

namespace Iris.Platform.Windows.SystemIntegration;

/// <summary>One send attempt. Uncertain acknowledgement never causes a second connect/write.</summary>
internal sealed class NamedPipeIpcClient(IpcIdentity identity, IpcPipeFactory factory, TimeProvider time) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private IpcPipe? _pipe;
    private IpcCancellation? _cancellation;
    private Task<IpcSendResult>? _run;
    private Task? _cleanup;
    private bool _closing, _disposed;

    internal Task<IpcSendResult> RunAsync(IpcCommand command, CancellationToken cancellationToken = default)
    {
        var frame = IpcProtocol.Encode(command);
        lock (_gate)
        {
            if (_closing || _run != null) throw IpcProtocol.Unavailable();
            _cancellation = new(time, IpcProtocol.Deadline, cancellationToken);
            return _run = RunCoreAsync(frame);
        }
    }
    private async Task<IpcSendResult> RunCoreAsync(byte[] frame)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool startedWrite = false;
        try
        {
            var token = _cancellation!.Token;
            token.ThrowIfCancellationRequested();
            _pipe = factory.CreateClient(identity);
            await _pipe.ConnectAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _pipe.VerifyPeer(server: false);
            token.ThrowIfCancellationRequested();
            startedWrite = true;
            await _pipe.Stream.WriteAsync(frame, token).ConfigureAwait(false);
            var reply = await IpcProtocol.ReadLineAsync(_pipe.Stream, IpcProtocol.MaxReplyBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return IpcProtocol.DecodeReply(reply);
        }
        catch (Exception) { return startedWrite ? IpcSendResult.Unconfirmed : IpcSendResult.Unavailable; }
        finally { Array.Clear(frame); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closing = true; _cancellation?.Request();
            if (_disposed) return ValueTask.CompletedTask;
            if (_cleanup is { IsCompleted: false }) return new(_cleanup);
            return new(_cleanup = CleanupAsync());
        }
    }
    private async Task CleanupAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (_run != null) await _run.ConfigureAwait(false);
            if (_cancellation != null) { await _cancellation.DisposeAsync().ConfigureAwait(false); _cancellation = null; }
            if (_pipe != null) { await _pipe.DisposeAsync().ConfigureAwait(false); _pipe = null; }
            lock (_gate) _disposed = true;
        }
        catch (Exception) { throw IpcProtocol.Unavailable(); }
    }
}
