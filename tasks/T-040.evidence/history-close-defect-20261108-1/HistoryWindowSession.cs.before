using System.ComponentModel;
using Iris.App.Capture;
using Iris.App.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.App.History;

internal sealed class HistoryWindowSession(IServiceScope scope, IUiDispatcher ui, WindowPreparationIntent intent, Action<HistoryWindowSession> disposed)
{
    private readonly Lock _gate = new();
    private Task? _cleanup;
    private bool _closing, _closed, _disposed;
    public HistoryWindow? Window { get; private set; }
    public HistoryViewModel? ViewModel { get; private set; }
    public bool IsClosing { get { lock (_gate) return _closing; } }
    public bool IsDisposed { get { lock (_gate) return _disposed; } }
    public Task? CleanupTask { get { lock (_gate) return _cleanup; } }

    internal void Show()
    {
        if (IsClosing || IsDisposed) return;
        if (Window == null)
        {
            ViewModel = scope.ServiceProvider.GetRequiredService<HistoryViewModel>();
            Window = scope.ServiceProvider.GetRequiredService<HistoryWindow>();
            Window.DataContext = ViewModel;
            Window.Closing += OnClosing;
            Window.Closed += OnClosed;
            ViewModel.RequestClose += CloseFromEvent;
        }
        var attempt = intent.BeginShow();
        bool shown = false;
        try
        {
            Window.Show();
            if (Window.WindowState == System.Windows.WindowState.Minimized) Window.WindowState = System.Windows.WindowState.Normal;
            Window.Activate();
            shown = true;
        }
        finally { attempt.Complete(Window, shown); }
        _ = RefreshFromShowAsync();
    }
    private async Task RefreshFromShowAsync()
    {
        try { if (ViewModel != null) await ViewModel.RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ViewModel?.LogFailure(ex); }
    }
    internal void StopAcceptance() => ViewModel?.StopAcceptance();
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (Window?.ForceClose == true) return;
        args.Cancel = true;
        CloseFromEvent();
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        lock (_gate) _closed = true;
        intent.Advance(hiding: true);
        CloseFromEvent();
    }
    private void CloseFromEvent() => _ = ObserveCloseAsync();
    private async Task ObserveCloseAsync()
    {
        try { await ShutdownAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            ViewModel?.LogFailure(ex);
        }
    }
    public Task ShutdownAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_cleanup is { IsCompleted: false } || _cleanup?.IsCompletedSuccessfully == true) return _cleanup;
            _closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _cleanup = completion.Task;
        }
        _ = CompleteCleanupAsync(completion);
        return completion.Task;
    }
    private async Task CompleteCleanupAsync(TaskCompletionSource completion)
    {
        try
        {
            StopAcceptance();
            intent.Advance(hiding: true);
            await ui.InvokeAsync(() => { ViewModel?.ShowClosing(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            if (ViewModel != null) await ViewModel.DrainAsync().ConfigureAwait(false);
            await ui.InvokeAsync(() =>
            {
                if (Window != null && !_closed)
                {
                    Window.ForceClose = true;
                    try { Window.Close(); } finally { Window.ForceClose = false; }
                    if (!_closed) throw new InvalidOperationException("History window close did not complete.");
                }
                ViewModel?.Dispose();
                scope.Dispose();
                if (Window != null)
                {
                    Window.Closing -= OnClosing; Window.Closed -= OnClosed; Window.DataContext = null;
                }
                if (ViewModel != null) ViewModel.RequestClose -= CloseFromEvent;
                lock (_gate) _disposed = true;
                disposed(this);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            Exception failure = ex;
            try { await ui.InvokeAsync(() => { ViewModel?.ShowClosing(failed: true); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception display) { failure = new AggregateException(failure, display); }
            completion.TrySetException(failure);
        }
    }
}
