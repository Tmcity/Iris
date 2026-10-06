using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Iris.Providers.Ocr.Baidu;

// Runs inside the OCR resilience/logging handlers so telemetry never receives raw transport
// messages, inner exceptions or Data. Exception categories used by the retry policy stay intact.
internal sealed class BaiduOcrTransportSafetyHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await base.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new SafeHttpFailure(ex.HttpRequestError, ex.StatusCode, ex.HResult); }
        catch (IOException ex) { throw new IOException("OCR transport IO failed.", ex.HResult); }
        catch (OperationCanceledException ex) { throw new OperationCanceledException("OCR transport canceled.", null, ex.CancellationToken); }
        catch (TimeoutRejectedException) { throw new TimeoutRejectedException("OCR transport timed out."); }
        catch (BrokenCircuitException) { throw new BrokenCircuitException("OCR transport circuit is open."); }
        catch (Exception ex) { throw new BaiduTransportFailure(BaiduErrors.Describe("TRANSPORT", ex)); }
    }
    private sealed class SafeHttpFailure : HttpRequestException
    {
        internal SafeHttpFailure(HttpRequestError error, HttpStatusCode? status, int hr)
            : base(error, "OCR transport failed.", null, status) { HResult = hr; }
    }
}

internal sealed class BaiduTransportFailure(string diagnostic) : Exception("OCR transport failed.")
{
    internal string Diagnostic { get; } = diagnostic;
}
