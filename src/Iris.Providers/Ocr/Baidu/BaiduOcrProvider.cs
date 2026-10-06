using Iris.Core.Common;
using Iris.Core.Errors;
using Iris.Core.Imaging;
using Iris.Core.Ocr;

namespace Iris.Providers.Ocr.Baidu;

internal sealed class BaiduOcrProvider(BaiduConfiguration configuration, IHttpClientFactory clients, TimeProvider time) : IOcrProvider, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _tokenGate = new(1), _cleanupGate = new(1);
    private readonly HashSet<BaiduCall> _calls = [];
    private readonly List<BaiduHttpExchange> _retained = [];
    private readonly List<BaiduCall> _retainedCalls = [];
    private TokenReceipt? _cached;
    private bool _stopping, _disposed;
    private Task? _disposal, _retry;
    private int _tokenWaiters;
    internal int TokenWaiterCount => Volatile.Read(ref _tokenWaiters);
    public ProviderDescriptor Descriptor => configuration.Descriptor;
    public ImageConstraints Constraints => configuration.Constraints;
    public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        Admit(async (call, _) =>
        {
            if (!Descriptor.IsConfigured) throw new BaiduException(ErrorCode.NotConfigured, "CONFIGURATION", Descriptor.Id);
            configuration.ValidateImage(request?.Image);
            call.Check();
            string encoded = Convert.ToBase64String(request!.Image.Bytes);
            call.Check();
            byte[] body;
            using (var form = configuration.Form(encoded, configuration.Language(request.LanguageHint)))
            {
                if (form.Headers.ContentLength > configuration.BodyLimit) throw new BaiduException(ErrorCode.ImageTooLarge, "FORM_BODY", Descriptor.Id);
                body = await form.ReadAsByteArrayAsync(call.Token).ConfigureAwait(false);
            }
            encoded = null!;
            call.Check();
            bool authenticationRetried = false, rateRetried = false;
            while (true)
            {
                var receipt = await GetTokenAsync(call, false, null).ConfigureAwait(false);
                var result = await SendAsync(configuration.RecognitionUri(receipt.Value), body, false, call,
                    (bytes, _) => BaiduResponseParser.Recognition(bytes, configuration, call.Check)).ConfigureAwait(false);
                if (result.BusinessCode == 0) return new OcrResult(Descriptor.Id, result.Lines!, null, TimeSpan.Zero);
                if (result.BusinessCode is 110 or 111 && !authenticationRetried)
                {
                    authenticationRetried = true;
                    lock (_gate) if (ReferenceEquals(_cached, receipt)) _cached = null;
                    continue;
                }
                if (result.BusinessCode == 18 && !rateRetried)
                {
                    rateRetried = true; call.Check();
                    await Task.Delay(TimeSpan.FromMilliseconds(500), time, call.Token).ConfigureAwait(false);
                    call.Check(); continue;
                }
                throw BaiduErrors.Business(result.BusinessCode, Descriptor.Id);
            }
        }, (value, elapsed) => value with { Elapsed = elapsed }, null, cancellationToken);

    public Task<ProviderTestResult> TestAsync(CancellationToken cancellationToken) =>
        Admit(async (call, initial) =>
        {
            if (!Descriptor.IsConfigured) throw new BaiduException(ErrorCode.NotConfigured, "CONFIGURATION", Descriptor.Id);
            await GetTokenAsync(call, true, initial).ConfigureAwait(false);
            return new ProviderTestResult(true, TimeSpan.Zero);
        }, (value, elapsed) => value with { Latency = elapsed },
            (error, elapsed) => new ProviderTestResult(false, elapsed, Error: error.ToErrorInfo()), cancellationToken);

    private Task<T> Admit<T>(Func<BaiduCall, TokenReceipt?, Task<T>> body, Func<T, TimeSpan, T> stamp,
        Func<BaiduException, TimeSpan, T>? failureResult, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping) throw new BaiduException(ErrorCode.Unknown, "STOPPED", Descriptor.Id);
            var call = new BaiduCall(time, configuration.Timeout, Descriptor.Id, caller);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var initial = _cached; _calls.Add(call);
            _ = Task.Run(async () =>
            {
                T? result = default; Exception? failure = null;
                try
                {
                    call.Start(); call.Check(); await CleanupPendingAsync().ConfigureAwait(false); call.Check();
                    result = await body(call, initial).ConfigureAwait(false);
                }
                catch (Exception ex) { failure = call.Classify("CALL", ex); }
                try { await call.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (call.HasPendingCleanup) { lock (_gate) _retainedCalls.Add(call); }
                    failure = new BaiduException(ErrorCode.Unknown, "CALL_CLEANUP", Descriptor.Id,
                        diagnostics: $"primary=[{(failure == null ? "none" : BaiduErrors.Describe("PRIMARY", failure))}];{BaiduErrors.Describe("CANCEL", ex)}");
                }
                lock (_gate)
                {
                    if (failure == null) { try { call.Check(); } catch (Exception ex) { failure = ex; } }
                    if (failure is OperationCanceledException) completion.TrySetCanceled(caller.IsCancellationRequested ? caller : new CancellationToken(true));
                    else if (failure is BaiduException error && failureResult != null) completion.TrySetResult(failureResult(error, time.GetElapsedTime(call.Started)));
                    else if (failure != null) completion.TrySetException(failure);
                    else completion.TrySetResult(stamp(result!, time.GetElapsedTime(call.Started)));
                    _calls.Remove(call); call.Done.TrySetResult();
                }
            }, CancellationToken.None);
            return completion.Task;
        }
    }

    private bool Valid(TokenReceipt? receipt) => receipt != null && time.GetElapsedTime(receipt.Started) < receipt.ReuseFor;
    private async Task<TokenReceipt> GetTokenAsync(BaiduCall call, bool test, TokenReceipt? initial)
    {
        call.Check();
        lock (_gate) if (Valid(_cached) && (!test || !ReferenceEquals(_cached, initial))) return _cached!;
        Interlocked.Increment(ref _tokenWaiters);
        try { await _tokenGate.WaitAsync(call.Token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _tokenWaiters); }
        try
        {
            call.Check();
            lock (_gate) if (Valid(_cached) && (!test || !ReferenceEquals(_cached, initial))) return _cached!;
            long started = time.GetTimestamp();
            var token = await SendAsync(configuration.TokenUri(), null, true, call,
                (bytes, status) => BaiduResponseParser.Token(bytes, status, Descriptor.Id)).ConfigureAwait(false);
            call.Check();
            if (time.GetElapsedTime(started) >= token.Ttl) throw new BaiduException(ErrorCode.InvalidResponse, "TOKEN_EXPIRED_ON_ARRIVAL", Descriptor.Id);
            var receipt = new TokenReceipt(token.Value, started, token.Ttl - TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(1).Ticks, token.Ttl.Ticks / 10)));
            lock (_gate) { call.Check(); _cached = receipt; }
            return receipt;
        }
        finally { _tokenGate.Release(); }
    }

    private async Task<T> SendAsync<T>(Uri uri, byte[]? form, bool token, BaiduCall call, Func<byte[], int, T> parse)
    {
        var exchange = new BaiduHttpExchange();
        T? result = default; Exception? failure = null;
        try { result = await exchange.ExecuteAsync(clients, uri, form, token ? 256 * 1024 : 16 * 1024 * 1024, token, call, Descriptor.Id, parse).ConfigureAwait(false); }
        catch (Exception ex) { failure = call.Classify(token ? "TOKEN" : "RECOGNIZE", ex); }
        var errors = await exchange.CleanupAsync().ConfigureAwait(false);
        if (errors.Count != 0)
        {
            lock (_gate) _retained.Add(exchange);
            throw new BaiduException(ErrorCode.Unknown, "HTTP_CLEANUP", Descriptor.Id,
                diagnostics: $"primary=[{(failure == null ? "none" : BaiduErrors.Describe("PRIMARY", failure))}];cleanup=[{string.Join(';', errors)}]");
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        call.Check(); return result!;
    }
    private async Task CleanupPendingAsync()
    {
        await _cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            BaiduHttpExchange[] pending; lock (_gate) pending = _retained.ToArray();
            var errors = new List<string>();
            foreach (var exchange in pending)
            {
                var current = await exchange.CleanupAsync().ConfigureAwait(false);
                if (current.Count == 0) { lock (_gate) _retained.Remove(exchange); } else errors.AddRange(current);
            }
            BaiduCall[] calls; lock (_gate) calls = _retainedCalls.ToArray();
            foreach (var call in calls)
            {
                try { await call.DisposeAsync().ConfigureAwait(false); lock (_gate) _retainedCalls.Remove(call); }
                catch (Exception ex) { errors.Add(BaiduErrors.Describe("CALL_CLEANUP", ex)); }
            }
            if (errors.Count != 0) throw new BaiduException(ErrorCode.Unknown, "RETRY_CLEANUP", Descriptor.Id, diagnostics: string.Join(';', errors));
        }
        finally { _cleanupGate.Release(); }
    }
    internal Task RetryCleanupAsync()
    {
        lock (_gate)
        {
            if (_disposal is { IsCompleted: false }) return _disposal;
            if (_disposed) return Task.CompletedTask;
            if (_retry is { IsCompleted: false }) return _retry;
            return _retry = Task.Run(CleanupPendingAsync);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_disposed) return ValueTask.CompletedTask;
            if (_disposal is { IsCompleted: false }) return new(_disposal);
            var calls = _calls.ToArray(); var retry = _retry;
            foreach (var call in calls) call.Cancel(true);
            return new(_disposal = Task.Run(async () =>
            {
                await Task.WhenAll(calls.Select(c => c.Done.Task)).ConfigureAwait(false);
                if (retry != null) { try { await retry.ConfigureAwait(false); } catch (BaiduException) { /* Retry current retained responsibilities below. */ } }
                await CleanupPendingAsync().ConfigureAwait(false);
                lock (_gate) { _cached = null; _disposed = true; _tokenGate.Dispose(); _cleanupGate.Dispose(); }
            }));
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_calls.Count != 0 || _retained.Count != 0 || _retainedCalls.Count != 0 || _disposal is { IsCompleted: false } || _retry is { IsCompleted: false })
                throw new BaiduException(ErrorCode.Unknown, "ASYNC_DISPOSE_REQUIRED", Descriptor.Id);
            _stopping = _disposed = true; _cached = null; _tokenGate.Dispose(); _cleanupGate.Dispose();
        }
    }
    private sealed class TokenReceipt(string value, long started, TimeSpan reuseFor)
    {
        internal string Value { get; } = value;
        internal long Started { get; } = started;
        internal TimeSpan ReuseFor { get; } = reuseFor;
    }
}
