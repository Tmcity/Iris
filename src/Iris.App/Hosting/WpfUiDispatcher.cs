using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Iris.App.Hosting;

/// <summary>
/// WPF UI 调度适配器：将异步委托调度至 WPF Dispatcher，并在外部完整等待内部 Task 完成。
/// </summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Func<Dispatcher?> _dispatcherResolver;

    public WpfUiDispatcher()
        : this(() => Application.Current?.Dispatcher)
    {
    }

    internal WpfUiDispatcher(Func<Dispatcher?> dispatcherResolver)
    {
        _dispatcherResolver = dispatcherResolver ?? throw new ArgumentNullException(nameof(dispatcherResolver));
    }

    public bool CheckAccess()
    {
        var dispatcher = _dispatcherResolver();
        return dispatcher?.CheckAccess() ?? false;
    }

    public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var dispatcher = _dispatcherResolver();
        if (dispatcher == null)
        {
            throw new InvalidOperationException("WPF Dispatcher is not available.");
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new OperationCanceledException("WPF Dispatcher is shutting down.");
        }

        if (dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await callback().ConfigureAwait(false);
            return;
        }

        DispatcherOperation<Task> operation;
        try
        {
            operation = dispatcher.InvokeAsync(callback, DispatcherPriority.Normal, cancellationToken);
        }
        catch (Exception) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new OperationCanceledException("WPF Dispatcher is shutting down.");
        }

        try
        {
            await operation.Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new OperationCanceledException("WPF Dispatcher is shutting down.");
        }
    }
}
