using Iris.Core.Imaging;
using Iris.Core.Platform;

namespace Iris.Platform.Windows.Capture;

internal sealed class GdiScreenCapturer(NativeGdiCaptureAdapter native, IDesktopContext desktop,
    ScreenMemoryEncoder encoder, ICaptureThreadFactory threads, TimeProvider time) : IScreenCapturer, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _active;
    private Task? _stop;
    private bool _disposed, _fullyStopped, _used;
    private volatile bool _stopping;
    private GdiCaptureWorker? _worker;

    public Task<CapturedFrame> CaptureVirtualScreenAsync(CancellationToken cancellationToken) => Admit(async check =>
    {
        check();
        if (_worker == null)
        {
            GdiCaptureWorker worker;
            try { worker = new(native, desktop, time, threads); }
            catch (Exception ex) { throw ScreenCaptureErrors.Combine("WORKER_CREATE", ex, [], cancellationToken.IsCancellationRequested || _stopping); }
            try { check(); worker.Start(); _worker = worker; }
            catch { worker.Dispose(); throw; }
        }
        return await _worker.CaptureAsync(check, () => cancellationToken.IsCancellationRequested || _stopping).ConfigureAwait(false);
    }, cancellationToken);

    public Task<ImageData> CropAsync(CapturedFrame frame, PixelRect region, ImageFormat format = ImageFormat.Png, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PixelRect local;
        try { local = ScreenPixelLayout.Intersection(frame, region, format); }
        catch (Exception ex) { throw ScreenCaptureErrors.Public(ex); }
        return Admit(async check =>
        {
            var pixels = ScreenPixelLayout.CopyCrop(frame, local, check);
            return await encoder.EncodeAsync(pixels, local.Width, local.Height, format, check,
                () => cancellationToken.IsCancellationRequested || _stopping).ConfigureAwait(false);
        }, cancellationToken);
    }
    private Task<T> Admit<T>(Func<Action, Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping) throw ScreenCaptureErrors.Public(ScreenCaptureErrors.Fail("STOPPED"));
            if (_active is { IsCompleted: false }) throw ScreenCaptureErrors.Public(ScreenCaptureErrors.Fail("BUSY"));
            _used = true;
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _active = drained.Task; // A non-result barrier cannot retain a returned frame as a hidden cache.
            _ = Task.Run(async () =>
            {
                void Check() { token.ThrowIfCancellationRequested(); if (_stopping) throw new OperationCanceledException(new CancellationToken(true)); }
                T? result = default;
                Exception? failure = null;
                try
                {
                    await CleanupPendingAsync(token).ConfigureAwait(false);
                    Check();
                    result = await operation(Check).ConfigureAwait(false);
                }
                catch (Exception ex) { failure = ex; }
                lock (_gate)
                {
                    if (failure == null) { try { Check(); } catch (OperationCanceledException ex) { failure = ex; } }
                    if (failure is OperationCanceledException) completion.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
                    else if (failure != null) completion.TrySetException(ScreenCaptureErrors.Public(failure));
                    else completion.TrySetResult(result!);
                    // Complete the public task before exposing an idle slot or releasing Stop's barrier.
                    drained.TrySetResult();
                    _active = null;
                }
            }, CancellationToken.None);
            return completion.Task;
        }
    }
    private async Task CleanupPendingAsync(CancellationToken caller = default)
    {
        var errors = new List<string>();
        if (_worker != null) errors.AddRange(await _worker.RetryCleanupAsync().ConfigureAwait(false));
        errors.AddRange(await encoder.RetryCleanupAsync().ConfigureAwait(false));
        if (errors.Count != 0) throw ScreenCaptureErrors.Combine("PENDING_CLEANUP", null, errors, _stopping || caller.IsCancellationRequested);
    }
    internal Task RetryCleanupAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is { IsCompleted: false }) return _stop;
            if (_fullyStopped) return Task.CompletedTask;
            if (_active is { IsCompleted: false }) throw ScreenCaptureErrors.Public(ScreenCaptureErrors.Fail("BUSY"));
            return _active = Task.Run(async () =>
            {
                try { await CleanupPendingAsync().ConfigureAwait(false); }
                catch (Exception ex) { throw ScreenCaptureErrors.Public(ex); }
            });
        }
    }
    internal void StopAcceptance() { lock (_gate) _stopping = true; }
    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_stop is { IsCompleted: false }) return _stop;
            if (_fullyStopped) return Task.CompletedTask;
            var active = _active;
            return _stop = Task.Run(async () =>
            {
                if (active != null) await active.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
                try
                {
                    await CleanupPendingAsync().ConfigureAwait(false);
                    if (_worker != null) { await _worker.StopAsync().ConfigureAwait(false); _worker = null; }
                    lock (_gate) _fullyStopped = true;
                }
                catch (Exception ex) { throw ScreenCaptureErrors.Public(ex); }
            });
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); Dispose(); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if ((_used && !_fullyStopped) || _active is { IsCompleted: false } || _stop is { IsCompleted: false })
                throw new InvalidOperationException("Asynchronous screen capture shutdown required.");
            _disposed = _stopping = _fullyStopped = true;
        }
    }
}
