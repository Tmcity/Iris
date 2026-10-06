using Iris.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Iris.Core.Workflows;

/// <summary>Owns forwarding callbacks until both the handler and cancellation work actually exit.</summary>
internal sealed class ScreenshotOcrCancellation : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly ILogger _logger;
    private Task? _cancellation;

    internal ScreenshotOcrCancellation(ILogger logger, CancellationToken caller, CancellationToken stopping)
    {
        _logger = logger;
        _callerRegistration = caller.Register(Cancel);
        _stopRegistration = stopping.Register(Cancel);
    }

    internal CancellationToken Token => _source.Token;

    private void Cancel()
    {
        lock (_gate) _cancellation ??= _source.CancelAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _callerRegistration.DisposeAsync().ConfigureAwait(false);
        await _stopRegistration.DisposeAsync().ConfigureAwait(false);
        Task? cancellation;
        lock (_gate) cancellation = _cancellation;
        try
        {
            if (cancellation != null) await cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var code = ScreenshotOcrWorkflow.ReportFailure(_logger, ex, CancellationToken.None, CancellationToken.None);
            throw new IrisException(code, ErrorMessages.GetDefault(code));
        }
        finally { _source.Dispose(); }
    }
}
