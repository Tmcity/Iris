using Iris.Core.Errors;
using Iris.Core.Network;
using Iris.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Iris.Providers.Http;

/// <summary>Owns independent, single-use HEAD sessions, including sessions whose cleanup failed.</summary>
internal sealed partial class ProxyConnectivityTester : IProxyConnectivityTester, IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<ProxyConnectivitySession> _sessions = [];
    private readonly INetworkProxySnapshotSource _source;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly NetworkRouteResolver _resolver;
    private readonly ProxyConnectivityTransportFactory _factory;
    private readonly ILogger<ProxyConnectivityTester> _logger;
    private bool _stopping;
    private Task? _stop;

    public ProxyConnectivityTester(INetworkProxySnapshotSource source, ISettingsService settings, TimeProvider time,
        NetworkRouteResolver resolver, ProxyConnectivityTransportFactory factory, ILogger<ProxyConnectivityTester> logger)
    { _source = source; _settings = settings; _time = time; _resolver = resolver; _factory = factory; _logger = logger; }

    public IProxyConnectivityTestSession Create(ProxyDraft draft, NetworkEndpointSelection endpoint)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(draft.BaseSettings); ArgumentNullException.ThrowIfNull(draft.TargetSettings);
        var copy = new ProxyDraft(draft.BaseSettings with { }, draft.TargetSettings with { }, draft.Credentials is { } edit
            ? edit with { Replacement = edit.Replacement is { } value ? value with { } : null } : null);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var session = new ProxyConnectivitySession(this, copy, endpoint with { }, _source, _settings, _time, _resolver, _factory);
            _sessions.Add(session);
            return session;
        }
    }

    internal int SessionCount { get { lock (_gate) return _sessions.Count; } }
    internal void Released(ProxyConnectivitySession session, TaskCompletionSource completion)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
            session.CompleteDispose();
            completion.TrySetResult();
        }
    }
    internal void StopAcceptance()
    {
        ProxyConnectivitySession[] sessions;
        lock (_gate) { _stopping = true; sessions = _sessions.ToArray(); }
        foreach (var session in sessions) session.StopAcceptance();
    }
    internal Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_stop is not { IsCompleted: false } && _stop?.IsCompletedSuccessfully != true) _stop = StopCoreAsync(_sessions.ToArray());
            return _stop ?? Task.CompletedTask;
        }
    }
    private async Task StopCoreAsync(ProxyConnectivitySession[] sessions)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var work = sessions.Select(async session =>
        {
            try { await session.DisposeAsync().ConfigureAwait(false); return true; }
            catch (Exception ex) { LogCleanupFailed(_logger, ex.GetType().Name); return false; }
        }).ToArray();
        if ((await Task.WhenAll(work).ConfigureAwait(false)).Any(success => !success)) throw NetworkProxyCodec.Unavailable();
    }
    public void Dispose()
    {
        StopAcceptance();
        lock (_gate) if (_sessions.Count != 0 || _stop is { IsCompleted: false }) throw NetworkProxyCodec.Unavailable();
    }
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Proxy connectivity cleanup incomplete: {ExceptionType}")]
    private static partial void LogCleanupFailed(ILogger logger, string exceptionType);
}

/// <summary>Reuses the frozen route callbacks, with HEAD-specific limits and no global pool.</summary>
internal class ProxyConnectivityTransportFactory : NetworkTransportFactory
{
    internal override HttpMessageHandler Create(NetworkRoute route)
    {
        var handler = (SocketsHttpHandler)base.Create(route);
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);
        handler.MaxResponseHeadersLength = 16;
        handler.UseCookies = false;
        return handler;
    }
}

internal sealed class NetworkTransportLifetime(NetworkTransportManager manager, ProxyConnectivityTester tester) : INetworkTransportLifetime
{
    private readonly Lock _gate = new();
    private Task? _stop;
    public void StopAcceptance() { manager.StopAcceptance(); tester.StopAcceptance(); }
    public Task StopAsync()
    {
        StopAcceptance();
        lock (_gate)
        {
            if (_stop == null || _stop.IsFaulted || _stop.IsCanceled) _stop = StopCoreAsync();
            return _stop;
        }
    }
    private async Task StopCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await Task.WhenAll(tester.StopAsync(), manager.StopAsync()).ConfigureAwait(false);
    }
}
