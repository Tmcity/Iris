using Iris.App.Capture;
using Iris.App.Hosting;
using Iris.Core.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.App.History;

internal interface IHistoryShutdownCoordinator
{
    void StopAcceptance();
    Task ShutdownAsync();
}

internal sealed class HistoryWindowPresenter : IHistoryWindowPresenter, IHistoryShutdownCoordinator, IScreenshotPreparationParticipant
{
    private readonly IServiceProvider _services;
    private readonly IUiDispatcher _ui;
    private readonly Lock _gate = new();
    private readonly WindowPreparationIntent _intent;
    private HistoryWindowSession? _session;
    private Task _showTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _stopped;

    public HistoryWindowPresenter(IServiceProvider services, IUiDispatcher ui)
    { _services = services; _ui = ui; _intent = new(_gate); }

    internal HistoryWindowSession? ActiveSession { get { lock (_gate) return _session; } }
    public WindowPreparationIntent.Receipt ObservePreparation(Action invalidated)
    {
        lock (_gate) return _intent.Observe(invalidated, _ui, () => _session?.Window,
            () => !_stopped && _session is { IsClosing: false, IsDisposed: false });
    }
    public Task ShowAsync()
    {
        TaskCompletionSource? completion = null;
        Action notify;
        Task task;
        lock (_gate)
        {
            if (_stopped) return Task.CompletedTask;
            notify = _intent.AdvanceLocked();
            if (_showTask.IsCompleted)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _showTask = completion.Task;
            }
            task = _showTask;
        }
        notify();
        if (completion != null) _ = CompleteShowAsync(completion);
        return task;
    }
    private async Task CompleteShowAsync(TaskCompletionSource completion)
    {
        try
        {
            HistoryWindowSession? previous;
            lock (_gate) previous = _session;
            if (previous is { IsClosing: true }) await previous.ShutdownAsync().ConfigureAwait(false);
            await _ui.InvokeAsync(async () =>
            {
                HistoryWindowSession? session;
                lock (_gate)
                {
                    if (_stopped) return;
                    session = _session;
                }
                if (session == null)
                {
                    session = new HistoryWindowSession(_services.CreateScope(), _ui, _intent, OnDisposed);
                    lock (_gate) _session = session;
                }
                try { session.Show(); }
                catch (Exception primary)
                {
                    try { await session.ShutdownAsync(); }
                    catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                    throw;
                }
            }, CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    private void OnDisposed(HistoryWindowSession session)
    { lock (_gate) if (ReferenceEquals(_session, session)) _session = null; }

    public void StopAcceptance()
    {
        Action notify;
        HistoryWindowSession? session;
        lock (_gate) { _stopped = true; notify = _intent.AdvanceLocked(hiding: true); session = _session; }
        session?.StopAcceptance();
        notify();
    }
    public Task ShutdownAsync()
    {
        StopAcceptance();
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdownTask is { IsCompleted: false } || _shutdownTask?.IsCompletedSuccessfully == true) return _shutdownTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _shutdownTask = completion.Task;
        }
        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }
    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        try
        {
            Task showing;
            lock (_gate) showing = _showTask;
            // Failed Show has already attempted rollback; the retained owner below retries any remaining cleanup.
            try { await showing.ConfigureAwait(false); } catch (Exception) { }
            HistoryWindowSession? session;
            lock (_gate) session = _session;
            if (session != null) await session.ShutdownAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
