using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Iris.Core.Settings;
using Iris.Providers.DependencyInjection;
using Iris.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

internal sealed record ManagedSnapshot(Guid Version, ProxyConfig Settings, ProxySecret? Credentials)
{
    public override string ToString() => "ManagedProxySnapshot";
}
internal sealed class FrozenCredentials(Uri allowed, ProxySecret secret) : ICredentials
{
    public NetworkCredential? GetCredential(Uri uri, string authType) => uri == allowed ? new(secret.UserName, secret.Password) : null;
}
internal sealed class FrozenProxy(Uri proxy, ICredentials? credentials) : IWebProxy
{
    public ICredentials? Credentials { get => credentials; set => throw new NotSupportedException(); }
    public Uri GetProxy(Uri destination) => proxy;
    public bool IsBypassed(Uri host) => false;
}
internal sealed record ResolvedRoute(Uri? Uri, ICredentials? Credentials, string CredentialKey, bool UsesDefault = false);
internal sealed class FrozenCredentialMap(Uri allowed, Dictionary<string, NetworkCredential> values) : ICredentials
{
    public NetworkCredential? GetCredential(Uri uri, string authType) => uri == allowed && values.TryGetValue(authType, out var value)
        ? ReferenceEquals(value, CredentialCache.DefaultNetworkCredentials) ? value : new(value.UserName, value.Password, value.Domain) : null;
}
internal static class ResolveRoute
{
    internal static ResolvedRoute Capture(ManagedSnapshot snapshot, Uri target, IWebProxy system)
    {
        var config = snapshot.Settings;
        if (config.ProxyMode == ProxyMode.None) return new(null, null, "none");
        var local = typeof(ProvidersServiceCollectionExtensions).Assembly.GetType("Iris.Providers.Http.DynamicWebProxy")!
            .GetMethod("IsLocalAddress", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        if (config.BypassProxyOnLocal && (bool)local.Invoke(null, [target])!) return new(null, null, "none");
        if (config.ProxyMode == ProxyMode.System)
        {
            // Resolve outside SocketsHttpHandler: callback exceptions must never become direct routing.
            if (system.IsBypassed(target)) return new(null, null, "none");
            var uri = system.GetProxy(target);
            if (uri == null || uri == target) return new(null, null, "none");
            if (uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) throw new ArgumentException("Unsupported system proxy scheme.");
            var originalUri = uri;
            uri = new UriBuilder(uri.Scheme, uri.IdnHost, uri.Port).Uri;
            var systemCredentials = system.Credentials ?? CredentialCache.DefaultCredentials;
            if (ReferenceEquals(systemCredentials, CredentialCache.DefaultCredentials)) return new(uri, systemCredentials, "default", true);
            var map = new Dictionary<string, NetworkCredential>(StringComparer.OrdinalIgnoreCase); var key = new StringBuilder(); bool usesDefault = false;
            foreach (string scheme in new[] { "Negotiate", "NTLM", "Digest", "Basic", "socks4", "socks4a", "socks5" })
            {
                var credential = systemCredentials.GetCredential(originalUri, scheme);
                if (credential == null) continue;
                bool isDefault = ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials); usesDefault |= isDefault;
                map.Add(scheme, isDefault ? credential : new(credential.UserName, credential.Password, credential.Domain));
                key.Append(scheme).Append('\0').Append(isDefault ? "default" : credential.UserName + "\0" + credential.Password + "\0" + credential.Domain).Append('\0');
            }
            return new(uri, new FrozenCredentialMap(uri, map), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()))), usesDefault);
        }
        if (config.ProxyMode != ProxyMode.Custom) throw new ArgumentException("Invalid proxy mode.");
        var endpoint = new Uri(ProxyFormat.Clean(config.CustomProxyUrl) ?? throw new ArgumentException("Missing proxy endpoint."));
        return new(endpoint, snapshot.Credentials == null ? null : new FrozenCredentials(endpoint, snapshot.Credentials), "snapshot-bound");
    }
}
internal sealed class GenerationPool(ProxyWire wire) : IDisposable
{
    internal sealed class Generation(Guid id, string routeKey, HttpMessageInvoker invoker)
    {
        internal Guid Id => id;
        internal string RouteKey => routeKey;
        internal HttpMessageInvoker Invoker => invoker;
        internal int Users;
        internal bool Retired, Disposed;
    }
    private readonly object _gate = new();
    private readonly List<Generation> _all = [];
    private Generation? _current;
    internal IReadOnlyList<Generation> Generations => _all;
    internal Lease Acquire(ManagedSnapshot snapshot, Uri target, IWebProxy system)
    {
        var route = ResolveRoute.Capture(snapshot, target, system);
        string routeKey = (route.Uri?.AbsoluteUri ?? "DIRECT") + "|" + route.CredentialKey;
        lock (_gate)
        {
            if (_current == null || _current.Id != snapshot.Version || _current.RouteKey != routeKey)
            {
                var handler = wire.Handler(route.Uri == null ? null : new FrozenProxy(route.Uri, route.Credentials));
                if (route.UsesDefault) handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                var created = new Generation(snapshot.Version, routeKey, new HttpMessageInvoker(handler));
                if (_current != null) { _current.Retired = true; TryDispose(_current); }
                _current = created; _all.Add(created);
            }
            _current.Users++; return new(this, _current);
        }
    }
    private static void TryDispose(Generation generation)
    { if (generation.Retired && generation.Users == 0 && !generation.Disposed) { generation.Invoker.Dispose(); generation.Disposed = true; } }
    internal sealed class Lease(GenerationPool owner, Generation generation) : IDisposable
    {
        private bool _returned;
        internal Generation Generation => generation;
        public void Dispose()
        {
            lock (owner._gate) { if (_returned) return; _returned = true; generation.Users--; TryDispose(generation); }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            Check.That(_all.All(g => g.Users == 0), "all response ownership must finish before transport pool disposal");
            foreach (var generation in _all) { generation.Retired = true; TryDispose(generation); }
        }
    }
}
internal sealed record LogicalContext(ManagedSnapshot Snapshot, GenerationPool.Lease Lease);
internal sealed class CaptureHandler(Func<ManagedSnapshot> capture, GenerationPool pools, IWebProxy system) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<LogicalContext> Key = new("iris.t036a.logical-proxy");
    internal int Captures;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var snapshot = capture(); Captures++;
        var lease = pools.Acquire(snapshot, request.RequestUri!, system); request.Options.Set(Key, new(snapshot, lease));
        try
        {
            var response = await base.SendAsync(request, ct);
            response.Content = new LeaseContent(response.Content, lease);
            return response;
        }
        catch { lease.Dispose(); throw; }
    }
    private sealed class LeaseContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly IDisposable _lease;
        private bool _disposed;
        internal LeaseContent(HttpContent inner, IDisposable lease)
        { _inner = inner; _lease = lease; foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => _inner.CopyToAsync(stream, ct);
        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken ct) => _inner.CopyTo(stream, context, ct);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => _inner.ReadAsStreamAsync(ct);
        protected override Stream CreateContentReadStream(CancellationToken ct) => _inner.ReadAsStream(ct);
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                // Borrowed streams do not own a separate transport lease. Response disposal follows joined reads.
                // Do not mark complete if content cleanup throws: retain the original owner for retry.
                _inner.Dispose(); base.Dispose(true); _lease.Dispose(); _disposed = true;
            }
            else base.Dispose(disposing);
        }
    }
}
internal sealed class RoutingHandler(Action firstAttempt) : HttpMessageHandler
{
    internal List<Guid> Versions = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Check.That(request.Options.TryGetValue(CaptureHandler.Key, out var context), "retry clone must preserve logical snapshot context");
        Versions.Add(context!.Snapshot.Version);
        if (Versions.Count == 1)
        {
            firstAttempt(); var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return response;
        }
        return await context.Lease.Generation.Invoker.SendAsync(request, ct);
    }
}
internal static class PipelineChecks
{
    internal static async Task Run()
    {
        foreach (var name in new[] { HttpClientNames.Llm, HttpClientNames.Ocr })
        {
            var a = new ManagedSnapshot(Guid.NewGuid(), new(ProxyMode.Custom, "http://proxy.example.test:8080/", false), new("synthetic-A", "synthetic-secret"));
            var b = a with { Version = Guid.NewGuid(), Credentials = new("synthetic-B", "synthetic-secret") }; var current = a;
            var wire = new ProxyWire(); using var pools = new GenerationPool(wire); var system = new WebProxy("http://system-proxy.example.test:8080");
            var capture = new CaptureHandler(() => current, pools, system); var router = new RoutingHandler(() => current = b);
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ISettingsService>(new MemorySettings()); services.AddIrisProviders();
            services.Configure<HttpClientFactoryOptions>(name, options => options.HttpMessageHandlerBuilderActions.Add(builder =>
            { builder.PrimaryHandler.Dispose(); builder.PrimaryHandler = router; builder.AdditionalHandlers.Insert(0, capture); }));
            using var provider = services.BuildServiceProvider(); using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            HttpResponseMessage? first = null, next = null;
            try
            {
                first = await client.SendAsync(RuntimeChecks.Request(), HttpCompletionOption.ResponseHeadersRead);
                Check.That(router.Versions.Count == 2 && router.Versions.All(x => x == a.Version) && capture.Captures == 1, "existing named-client resilience must stay on captured A");
                next = await client.SendAsync(RuntimeChecks.Request(), HttpCompletionOption.ResponseHeadersRead);
                Check.That(router.Versions.Last() == b.Version && wire.Connects == 2, "new logical request B uses new handler/pool");
                Check.That(wire.AuthorizationLabels.SequenceEqual(new[] { "A", "B" }), "actual runtime A/B credentials remain isolated");
                Check.That(!pools.Generations[0].Disposed && pools.Generations[0].Retired, "held response retains retired pool");
                first.Dispose(); Check.That(pools.Generations[0].Disposed, "retired pool released after response ownership returns");
                Check.Pass("actual named pipeline " + name + ": outerCaptures=2 retryA=true nextB=true separateRuntimePools=true responseLifetime=true");
            }
            finally { first?.Dispose(); next?.Dispose(); }
        }
        foreach (var scheme in new[] { "http", "https", "socks5" })
        {
            var snapshot = new ManagedSnapshot(Guid.NewGuid(), new(ProxyMode.Custom, scheme + "://proxy.example.test:8080/", false), new("synthetic", "synthetic-secret"));
            var route = ResolveRoute.Capture(snapshot, new("https://service.example.test"), new WebProxy());
            Check.That(route.Uri!.Scheme == scheme && route.Credentials!.GetCredential(new("http://different-proxy.example.test"), "Basic") == null, "route credential binding");
        }
        var none = new ManagedSnapshot(Guid.NewGuid(), new(ProxyMode.None, "not-a-uri", false), null);
        Check.That(ResolveRoute.Capture(none, new("https://service.example.test"), new WebProxy()).Uri == null, "explicit None is direct even when dormant URL invalid");
        var local = none with { Settings = new(ProxyMode.Custom, "http://proxy.example.test", true) };
        Check.That(ResolveRoute.Capture(local, new("http://localhost"), new WebProxy()).Uri == null, "local bypass uses actual Iris classification");
        bool blocked = false; var events = new ConcurrentQueue<string>();
        try { ResolveRoute.Capture(none with { Settings = new(ProxyMode.System, null, false) }, new("http://service.example.test"), new CallbackProxy(new("http://proxy.example.test"), null, events) { ThrowGet = true }); }
        catch (InvalidOperationException) { blocked = true; }
        Check.That(blocked, "system resolution failure rejected before transport");
        Check.Pass("proposal System/None/Custom http-https-socks5 resolution, local bypass and fail-closed pre-transport guard; TLS/SOCKS wire not exercised");
        await DifferentRoutesWithinSameSnapshot();
    }
    private static async Task DifferentRoutesWithinSameSnapshot()
    {
        var system = new WebProxy("http://proxy.example.test:8080") { Credentials = new NetworkCredential("synthetic-A", "synthetic-secret") };
        var snapshot = new ManagedSnapshot(Guid.NewGuid(), new(ProxyMode.System, null, false), null); var wire = new ProxyWire();
        using (var pools = new GenerationPool(wire))
        {
            using (var lease = pools.Acquire(snapshot, new("http://service.example.test"), system)) using (await lease.Generation.Invoker.SendAsync(RuntimeChecks.Request(), CancellationToken.None)) { }
            system.Credentials = new NetworkCredential("synthetic-B", "synthetic-secret");
            using (var lease = pools.Acquire(snapshot, new("http://service.example.test"), system)) using (await lease.Generation.Invoker.SendAsync(RuntimeChecks.Request(), CancellationToken.None)) { }
            Check.That(wire.Connects == 2 && wire.AuthorizationLabels.SequenceEqual(new[] { "A", "B" }), "same-config System credential replacement needs distinct pool key");
        }
        var localWire = new ProxyWire(requireAuth: false);
        using (var pools = new GenerationPool(localWire))
        {
            var custom = snapshot with { Settings = new(ProxyMode.Custom, "http://proxy.example.test:8080", true) };
            using (var lease = pools.Acquire(custom, new("http://service.example.test"), system)) using (await lease.Generation.Invoker.SendAsync(RuntimeChecks.Request(), CancellationToken.None)) { }
            using (var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/probe"))
            using (var lease = pools.Acquire(custom, request.RequestUri!, system)) using (await lease.Generation.Invoker.SendAsync(request, CancellationToken.None)) { }
            Check.That(localWire.Endpoints.SequenceEqual(new[] { "proxy.example.test:8080", "localhost:80" }), "local bypass must not accidentally reuse proxied handler in same config epoch");
        }
        Check.Pass("runtime same snapshot: System credential value and direct/proxy route are part of pool identity");
    }
}
