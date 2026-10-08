using Iris.Core.Actions;
using Iris.Platform.Windows.SystemIntegration;
using Microsoft.Extensions.Logging;

namespace Iris.App.Hosting;

internal sealed partial class IpcCommandCoordinator : IDisposable
{
    internal const int MaxDispatches = 16;
    private readonly Lock _gate = new();
    private readonly NamedPipeIpcServer _server;
    private readonly IActionDispatcher _dispatcher;
    private readonly ILogger<IpcCommandCoordinator> _logger;
    private readonly HashSet<Task> _work = [];
    private bool _started, _closing;

    public IpcCommandCoordinator(IpcIdentity identity, IpcPipeFactory factory, TimeProvider time, IActionDispatcher dispatcher,
        ILoggerFactory loggerFactory)
    {
        _server = new(identity, factory, time, loggerFactory.CreateLogger<NamedPipeIpcServer>());
        _dispatcher = dispatcher; _logger = loggerFactory.CreateLogger<IpcCommandCoordinator>();
    }
    internal void Start(IpcCommand? initialCommand)
    {
        lock (_gate)
        {
            if (_closing || _started) return;
            _server.Start(TryAccept); _started = true;
            if (initialCommand != null) TryAccept(initialCommand);
        }
    }
    internal IpcReply TryAccept(IpcCommand command)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closing || !_started) return IpcReply.Stopping;
            if (!IpcProtocol.IsValid(command)) return IpcReply.Invalid;
            if (_work.Count >= MaxDispatches) return IpcReply.Busy;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _work.Add(completion.Task);
        }
        _ = DispatchOwnedAsync(command, completion);
        return IpcReply.Accepted;
    }
    private async Task DispatchOwnedAsync(IpcCommand command, TaskCompletionSource completion)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            bool closing; lock (_gate) closing = _closing;
            if (!closing) await _dispatcher.DispatchAsync(command.Action, new(ActionTrigger.CommandLine, Payload: command.Payload)).ConfigureAwait(false);
        }
        catch (Exception ex) { LogDispatchFailed(_logger, ex.GetType().Name); }
        finally { lock (_gate) { _work.Remove(completion.Task); completion.TrySetResult(); } }
    }
    internal void StopAcceptance()
    {
        lock (_gate) _closing = true;
        _server.StopAcceptance();
    }
    internal Task StopTransportAsync() { StopAcceptance(); return _server.StopAsync(); }
    internal Task WaitForDispatchesAsync()
    {
        lock (_gate) return Task.WhenAll(_work.ToArray());
    }
    public void Dispose()
    {
        StopAcceptance();
        // Synchronous Host disposal is a fallback only. Never pretend it joined async pipe work.
        var stop = _server.StopAsync();
        if (!stop.IsCompletedSuccessfully || !WaitForDispatchesAsync().IsCompletedSuccessfully) throw IpcProtocol.Unavailable();
    }
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "IPC dispatch failed: {ExceptionType}")]
    private static partial void LogDispatchFailed(ILogger logger, string exceptionType);
}
