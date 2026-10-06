using System.Runtime.ExceptionServices;
using Iris.Core.Errors;
using Iris.Core.Imaging;

namespace Iris.Core.Ocr;

internal sealed class OcrService(IOcrProviderRegistry registry, IImagePreprocessor preprocessor, IOcrTextNormalizer normalizer)
    : IOcrService, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Operation> _operations = [];
    private readonly HashSet<Debt> _debts = [];
    private bool _stopping;
    private Task? _stop;

    public Task<OcrOutcome> RecognizeAsync(ImageData image, string? providerId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image); cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var operation = new Operation(cancellationToken); _operations.Add(operation);
            var task = Task.Run(() => RunAsync(operation, image, providerId), CancellationToken.None);
            _ = task.ContinueWith(done =>
            {
                _ = done.Exception;
                lock (_gate) { _operations.Remove(operation); operation.Done.TrySetResult(); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    private async Task<OcrOutcome> RunAsync(Operation operation, ImageData image, string? providerId)
    {
        IOcrProviderSnapshot? snapshot = null; OcrOutcome? result = null; Exception? primary = null;
        string? cleanup = null;
        try
        {
            operation.Check(); snapshot = await registry.AcquireSnapshotAsync(operation.Token).ConfigureAwait(false);
            operation.Check();
            var settings = snapshot.Settings.Ocr;
            var main = snapshot.Providers.FirstOrDefault(p => string.Equals(p.Profile.Id, providerId ?? settings.ActiveProviderId, StringComparison.Ordinal));
            var fallback = snapshot.Providers.FirstOrDefault(p => string.Equals(p.Profile.Id, settings.FallbackProviderId, StringComparison.Ordinal));
            if (main != null && fallback != null && (ReferenceEquals(main.Provider, fallback.Provider) || main.InstanceId == fallback.InstanceId || main.Profile.Id == fallback.Profile.Id)) fallback = null;
            OcrResult raw; bool usedFallback = false;
            try { raw = await Attempt(main, image, settings.LanguageHint, operation).ConfigureAwait(false); }
            catch (IrisException ex) when (CanFallback(ex.Code) && fallback != null)
            {
                operation.Check(); usedFallback = true;
                raw = await Attempt(fallback, image, settings.LanguageHint, operation).ConfigureAwait(false);
            }
            operation.Check();
            var text = normalizer.Normalize(raw.Lines, settings.TextLayout);
            operation.Check();
            if (string.IsNullOrWhiteSpace(text)) throw new IrisException(ErrorCode.OcrNoTextFound, ErrorMessages.GetDefault(ErrorCode.OcrNoTextFound), providerId: raw.ProviderId);
            result = new(raw, text, usedFallback);
        }
        catch (Exception ex) { primary = ex; }
        if (snapshot != null)
        {
            var debt = new Debt(snapshot); lock (_gate) _debts.Add(debt);
            try { await Clean(debt).ConfigureAwait(false); } catch (Exception ex) { cleanup = Metadata(ex); }
        }
        // Dispose registration before the CTS; await all scheduled cancellation callbacks.
        await operation.DisposeAsync().ConfigureAwait(false);
        if (cleanup != null || operation.CancelError != null)
            throw Failure("RELEASE", $"primary={Metadata(primary)};snapshot={cleanup ?? "none"};cancel={operation.CancelError ?? "none"}");
        operation.CheckCallerAndStop();
        if (primary != null)
        {
            if (primary is IrisException or OperationCanceledException) ExceptionDispatchInfo.Capture(primary).Throw();
            throw Failure("OPERATION", Metadata(primary));
        }
        return result!;
    }
    private async Task<OcrResult> Attempt(OcrProviderRegistration? item, ImageData original, string hint, Operation operation)
    {
        operation.Check();
        if (item == null || !item.Provider.Descriptor.IsConfigured)
            throw new IrisException(ErrorCode.NotConfigured, ErrorMessages.GetDefault(ErrorCode.NotConfigured), providerId: item?.Profile.Id);
        operation.Check(); var constraints = item.Provider.Constraints;
        operation.Check(); var prepared = await preprocessor.PrepareAsync(original, constraints, operation.Token).ConfigureAwait(false);
        operation.Check(); var raw = await item.Provider.RecognizeAsync(new(prepared, hint), operation.Token).ConfigureAwait(false);
        operation.Check(); return raw;
    }
    private static bool CanFallback(ErrorCode code) => code is ErrorCode.NotConfigured or ErrorCode.NetworkUnavailable or ErrorCode.Timeout or ErrorCode.ProviderUnavailable or ErrorCode.QuotaExceeded;
    private static string Metadata(Exception? error) => error == null ? "none" : error is IrisException iris ? $"code={(int)iris.Code}" : $"hr=0x{error.HResult:X8}";
    private static IrisException Failure(string stage, string detail) => new(ErrorCode.Unknown, ErrorMessages.GetDefault(ErrorCode.Unknown), detail: $"OCR_SERVICE:{stage};{detail}");

    private Task Clean(Debt debt)
    {
        lock (_gate)
        {
            if (!_debts.Contains(debt)) return Task.CompletedTask;
            if (debt.Work is { IsCompleted: false }) return debt.Work;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            debt.Work = completion.Task;
            _ = Task.Run(async () =>
            {
                Exception? error = null;
                try { await debt.Snapshot!.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { error = Failure("SNAPSHOT", Metadata(ex)); }
                lock (_gate)
                {
                    if (error == null) { _debts.Remove(debt); debt.Snapshot = null; completion.TrySetResult(); }
                    else { completion.TrySetException(error); _ = completion.Task.Exception; }
                }
            });
            return completion.Task;
        }
    }
    public Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task work; lock (_gate) work = CleanAll(_debts.Select(Clean).ToArray());
        Observe(work); return work.WaitAsync(cancellationToken);
    }
    private static async Task CleanAll(Task[] tasks)
    {
        var errors = new List<string>();
        foreach (var task in tasks) try { await task.ConfigureAwait(false); } catch (Exception ex) { errors.Add(Metadata(ex)); }
        if (errors.Count != 0) throw Failure("CLEANUP", string.Join(';', errors));
    }
    public void StopAccepting()
    {
        lock (_gate)
        {
            if (_stopping) return; _stopping = true;
            foreach (var operation in _operations) operation.RequestCancel();
        }
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopAccepting();
        lock (_gate)
        {
            if (_stop is not { IsCompleted: false }) { _stop = StopCore(_operations.ToArray()); Observe(_stop); }
            return _stop.WaitAsync(cancellationToken);
        }
    }
    private async Task StopCore(Operation[] operations)
    {
        foreach (var operation in operations) await operation.Done.Task.ConfigureAwait(false);
        await RetryCleanupAsync().ConfigureAwait(false);
        if (operations.Any(o => o.CancelError != null)) throw Failure("CANCEL", string.Join(';', operations.Where(o => o.CancelError != null).Select(o => o.CancelError)));
    }
    private static void Observe(Task task) => _ = task.ContinueWith(static done => { _ = done.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public ValueTask DisposeAsync() => new(StopAsync());
    public void Dispose()
    {
        StopAccepting();
        lock (_gate) if (_operations.Count != 0 || _debts.Count != 0 || _stop is { IsCompleted: false }) throw Failure("ASYNC_STOP_REQUIRED", "pending");
    }
    private sealed class Debt(IOcrProviderSnapshot snapshot)
    {
        internal IOcrProviderSnapshot? Snapshot = snapshot;
        internal Task? Work;
    }
    private sealed class Operation : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _registration;
        private Task? _cancel;
        private bool _finished, _cancelRequested;
        internal string? CancelError;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; }
        internal Operation(CancellationToken caller)
        {
            _caller = caller; Token = _source.Token;
            _registration = caller.UnsafeRegister(static state => ((Operation)state!).RequestCancel(), this);
        }
        internal void RequestCancel()
        {
            lock (_gate)
            {
                if (_cancel != null) return;
                _cancelRequested = true;
                if (_finished) return;
                _cancel = Task.Run(async () =>
                {
                    try { await _source.CancelAsync().ConfigureAwait(false); }
                    catch (Exception ex) { CancelError = Metadata(ex); }
                });
            }
        }
        internal void Check() { CheckCallerAndStop(); Token.ThrowIfCancellationRequested(); }
        internal void CheckCallerAndStop()
        {
            _caller.ThrowIfCancellationRequested();
            lock (_gate) if (_cancelRequested) throw new OperationCanceledException(Token);
        }
        public async ValueTask DisposeAsync()
        {
            await _registration.DisposeAsync().ConfigureAwait(false);
            Task? cancel;
            lock (_gate) { _finished = true; cancel = _cancel; }
            if (cancel != null) await cancel.ConfigureAwait(false);
            _source.Dispose();
        }
    }
}
