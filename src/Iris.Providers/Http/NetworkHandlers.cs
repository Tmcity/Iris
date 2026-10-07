using Iris.Core.Network;

namespace Iris.Providers.Http;

internal sealed class NetworkSnapshotHandler(string family, INetworkProxySnapshotSource source, NetworkTransportManager manager) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<NetworkTransportManager.Lease> ContextKey = new("Iris.Network.LogicalRequest");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var admission = manager.BeginRequest();
        if (request.Headers.Contains("Proxy-Authorization")
            || request.Content?.Headers.Any(h => h.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) == true)
            throw NetworkProxyCodec.Invalid();
        var snapshot = await source.CaptureCurrentAsync(request.RequestUri ?? throw NetworkProxyCodec.Invalid(), cancellationToken).ConfigureAwait(false);
        using var lease = manager.Acquire(family, snapshot, admission);
        request.Options.Set(ContextKey, lease);
        try { return await base.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { ((IDictionary<string, object?>)request.Options).Remove(ContextKey.Key); }
    }
}

internal sealed class NetworkRoutingHandler(NetworkTransportManager manager) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(NetworkSnapshotHandler.ContextKey, out var lease)) throw NetworkProxyCodec.Unavailable();
        try
        {
            var response = await lease.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Content = manager.OwnContent(lease, response.Content);
            return response;
        }
        catch (HttpRequestException ex)
        {
            manager.MarkFailed(lease.Entry);
            throw new SafeNetworkRequestFailure(ex);
        }
        catch (OperationCanceledException ex)
        {
            manager.MarkFailed(lease.Entry);
            throw new OperationCanceledException("Network request canceled.", null, ex.CancellationToken);
        }
        catch (IOException ex)
        {
            manager.MarkFailed(lease.Entry);
            throw new IOException("Network transport IO failed.", ex.HResult);
        }
        catch { manager.MarkFailed(lease.Entry); throw NetworkProxyCodec.Unavailable(); }
    }

    private sealed class SafeNetworkRequestFailure : HttpRequestException
    {
        internal SafeNetworkRequestFailure(HttpRequestException error)
            : base(error.HttpRequestError, "Network transport failed.", null, error.StatusCode) { HResult = error.HResult; }
    }
}
