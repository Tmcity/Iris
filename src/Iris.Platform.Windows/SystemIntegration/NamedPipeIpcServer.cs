using Iris.Core.Actions;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.SystemIntegration;

/// <summary>One persistent first-instance pipe, one bounded connection at a time; Dispatch is owned by App.</summary>
internal sealed partial class NamedPipeIpcServer : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly IpcIdentity _identity;
    private readonly IpcPipeFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<NamedPipeIpcServer> _logger;
    private readonly IpcCancellation _stop;
    private IpcPipe? _pipe;
    private IpcCancellation? _connection;
    private Task? _loop, _cleanup;
    private bool _started, _closing, _loopJoined, _disposed;

    internal NamedPipeIpcServer(IpcIdentity identity, IpcPipeFactory factory, TimeProvider time, ILogger<NamedPipeIpcServer> logger)
    { _identity = identity; _factory = factory; _time = time; _logger = logger; _stop = new(time); }

    internal void Start(Func<IpcCommand, IpcReply> accept)
    {
        lock (_gate)
        {
            if (_closing) return;
            if (_started) return;
            _started = true;
            try { _pipe = _factory.CreateServer(_identity); }
            catch (Exception) { _closing = true; throw IpcProtocol.Unavailable(); }
            _loop = ReceiveAsync(accept);
        }
    }
    private async Task ReceiveAsync(Func<IpcCommand, IpcReply> accept)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            while (!_stop.Token.IsCancellationRequested)
            {
                await _pipe!.AcceptAsync(_stop.Token).ConfigureAwait(false);
                _stop.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _connection = new(_time, IpcProtocol.Deadline, _stop.Token);
                    if (_closing) _connection.Request();
                }
                try
                {
                    var token = _connection.Token;
                    token.ThrowIfCancellationRequested();
                    _pipe.VerifyPeer(server: true);
                    IpcReply reply;
                    try
                    {
                        var bytes = await IpcProtocol.ReadLineAsync(_pipe.Stream, IpcProtocol.MaxFrameBytes, token).ConfigureAwait(false);
                        IpcCommand command;
                        try { command = IpcProtocol.Decode(bytes); }
                        finally { Array.Clear(bytes); }
                        token.ThrowIfCancellationRequested();
                        reply = accept(command);
                    }
                    catch (Iris.Core.Errors.IrisException) { reply = IpcReply.Invalid; }
                    token.ThrowIfCancellationRequested();
                    await _pipe.Stream.WriteAsync(IpcProtocol.ReplyBytes(reply), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // The client closes after consuming its reply. Disconnect must not discard an unread ACK.
                    // This bounded wait never parses or dispatches a second frame.
                    await _pipe.Stream.ReadAsync(new byte[1], token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Debug)) { string type = ex.GetType().Name; LogConnectionEnded(_logger, type); }
                }
                // Cleanup failures end the loop and retain the original objects for Stop retry.
                await _connection.DisposeAsync().ConfigureAwait(false); _connection = null;
                _pipe.Disconnect();
            }
        }
        catch (OperationCanceledException) when (_stop.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) { string type = ex.GetType().Name; LogConnectionEnded(_logger, type); }
            throw IpcProtocol.Unavailable();
        }
    }
    internal void StopAcceptance()
    {
        lock (_gate)
        {
            _closing = true;
            // Seal the current phase synchronously, without waiting for linked async callbacks to run.
            _connection?.Request();
            _stop.Request();
        }
    }
    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_cleanup is { IsCompleted: false }) return _cleanup;
            return _cleanup = CleanupAsync();
        }
    }
    private async Task CleanupAsync()
    {
        if (_loop != null) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (_loop != null && !_loopJoined)
            {
                try { await _loop.ConfigureAwait(false); }
                finally { _loopJoined = true; }
            }
            if (_connection != null) { await _connection.DisposeAsync().ConfigureAwait(false); _connection = null; }
            if (_pipe != null) { await _pipe.DisposeAsync().ConfigureAwait(false); _pipe = null; }
            await _stop.DisposeAsync().ConfigureAwait(false);
            lock (_gate) _disposed = true;
        }
        catch (Exception) { throw IpcProtocol.Unavailable(); }
    }
    public ValueTask DisposeAsync() => new(StopAsync());

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "IPC connection ended: {ExceptionType}")]
    private static partial void LogConnectionEnded(ILogger logger, string exceptionType);
}
