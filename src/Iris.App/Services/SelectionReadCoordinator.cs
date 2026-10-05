using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Platform.Windows.Selection;

namespace Iris.App.Services;

/// <summary>只协调同一个平台读取器；内层事务与 pending cleanup 仍由 Composite 持有。</summary>
internal sealed class SelectionReadCoordinator(CompositeSelectionReader reader) : ISelectionTextReader, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private Request? _actual;
    private Request? _pending;
    private Task? _stopTask;
    private bool _stopping;
    private bool _fullyStopped;
    private bool _used;
    private bool _disposed;
    private IrisException? _cancellationFailure;

    public Task<SelectionReadResult?> ReadAsync(SelectionReadOptions options, CancellationToken cancellationToken) =>
        Admit(options, passive: false, cancellationToken);

    internal Task<SelectionReadResult?> ReadPassiveAsync(SelectionReadOptions options, CancellationToken cancellationToken) =>
        Admit(options, passive: true, cancellationToken);

    private Task<SelectionReadResult?> Admit(SelectionReadOptions options, bool passive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frozen = SelectionReadOptionsSnapshot.Freeze(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
                return passive ? Task.FromResult<SelectionReadResult?>(null) : Task.FromCanceled<SelectionReadResult?>(new CancellationToken(true));
            if (_actual != null || _pending != null)
            {
                if (passive) return Task.FromResult<SelectionReadResult?>(null);
                if (_pending != null || _actual is not { Passive: true }) throw Failure("BUSY");
            }
            var request = new Request(frozen, passive, _actual, cancellationToken);
            _used = true;
            if (_actual == null) _actual = request;
            else
            {
                _pending = request;
                CancelLocked(_actual);
            }
            // This callback only records intent and schedules owned cancellation, including inline registration.
            request.Registration = cancellationToken.UnsafeRegister(_ =>
            {
                lock (_gate) CancelLocked(request);
            }, null);
            _ = Task.Run(() => ExecuteAsync(request), CancellationToken.None);
            return request.Completion.Task;
        }
    }

    private static void CancelLocked(Request request)
    {
        request.Cancelled = true;
        if (request.Sealed || request.Cancellation != null) return;
        // CancelAsync may execute arbitrary platform/OS-boundary callbacks. Never invoke it under the state lock.
        request.Cancellation = Task.Run(async () => await request.Source.CancelAsync().ConfigureAwait(false), CancellationToken.None);
    }

    private async Task ExecuteAsync(Request request)
    {
        SelectionReadResult? result = null;
        Exception? failure = null;
        try
        {
            if (request.Predecessor != null) await request.Predecessor.Drained.Task.ConfigureAwait(false);
            bool execute;
            lock (_gate)
            {
                execute = !request.Cancelled && !request.Caller.IsCancellationRequested && !_stopping;
                if (execute && _pending == request)
                {
                    _pending = null;
                    _actual = request;
                }
            }
            if (execute) result = await reader.ReadAsync(request.Options, request.Source.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            await request.Registration.DisposeAsync().ConfigureAwait(false);
            Task? cancellation;
            lock (_gate) { request.Sealed = true; cancellation = request.Cancellation; }
            if (cancellation != null)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Preserve both phases numerically without exposing arbitrary callback exception text.
                    failure = Failure("CANCEL", failure, ex);
                    lock (_gate) _cancellationFailure ??= (IrisException)failure;
                }
            }
            request.Dispose();
            lock (_gate)
            {
                if (_actual == request) _actual = null;
                if (_pending == request) _pending = null;
                if (failure != null && failure is not OperationCanceledException) request.Completion.TrySetException(failure);
                else if (failure is OperationCanceledException || request.Cancelled || request.Caller.IsCancellationRequested || _stopping)
                    request.Completion.TrySetCanceled(request.Caller.IsCancellationRequested ? request.Caller : new CancellationToken(true));
                else request.Completion.TrySetResult(result);
                request.Drained.TrySetResult();
            }
        }
    }

    internal void StopAcceptance()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_actual != null) CancelLocked(_actual);
            if (_pending != null) CancelLocked(_pending);
        }
    }

    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_fullyStopped) return Task.CompletedTask;
            var actual = _actual?.Drained.Task ?? Task.CompletedTask;
            var pending = _pending?.Drained.Task ?? Task.CompletedTask;
            return _stopTask = Task.Run(async () =>
            {
                await Task.WhenAll(actual, pending).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_cancellationFailure is { } failure)
                    {
                        _cancellationFailure = null;
                        throw failure;
                    }
                    _fullyStopped = true;
                }
            }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if ((_used && !_fullyStopped) || _actual != null || _pending != null || _stopTask is { IsCompleted: false })
                throw new InvalidOperationException("Asynchronous selection coordination shutdown required.");
            _disposed = _stopping = _fullyStopped = true;
        }
    }

    private static IrisException Failure(string stage, Exception? body = null, Exception? cleanup = null) =>
        new(ErrorCode.CaptureFailed, $"SELECTION_COORDINATION_{stage} hr=0x{body?.HResult ?? 0:X8} cleanup=0x{cleanup?.HResult ?? 0:X8}");

    private sealed class Request(SelectionReadOptions options, bool passive, Request? predecessor, CancellationToken caller) : IDisposable
    {
        internal readonly SelectionReadOptions Options = options;
        internal readonly bool Passive = passive;
        internal readonly CancellationToken Caller = caller;
        internal readonly Request? Predecessor = predecessor;
        internal readonly CancellationTokenSource Source = new();
        internal readonly TaskCompletionSource<SelectionReadResult?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal Task? Cancellation;
        internal bool Cancelled;
        internal bool Sealed;
        public void Dispose() => Source.Dispose();
    }
}
