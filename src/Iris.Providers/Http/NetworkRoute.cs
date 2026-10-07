using System.Net;
using System.Security.Cryptography;
using System.Text;
using Iris.Core.Network;
using Iris.Core.Settings;

namespace Iris.Providers.Http;

/// <summary>Only these immutable callbacks may enter SocketsHttpHandler.</summary>
internal sealed class NetworkRoute
{
    internal Uri? ProxyUri { get; init; }
    internal ProxyRouteKind Kind { get; init; }
    internal bool UsesDefaultCredentials { get; init; }
    internal bool AllDefaultCredentials { get; init; }
    internal IReadOnlyDictionary<string, NetworkCredential?> Credentials { get; init; } = new Dictionary<string, NetworkCredential?>();
    internal string Identity { get; init; } = "";
    public override string ToString() => "NetworkRoute";

    internal IWebProxy? CreateProxy() => ProxyUri == null ? null : new FrozenProxy(this);

    private sealed class FrozenProxy(NetworkRoute route) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => route.AllDefaultCredentials ? CredentialCache.DefaultCredentials : new FrozenCredentials(route);
            set => throw new NotSupportedException();
        }
        public Uri? GetProxy(Uri destination) => route.ProxyUri;
        public bool IsBypassed(Uri host) => false;
    }

    private sealed class FrozenCredentials(NetworkRoute route) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (uri != route.ProxyUri || !route.Credentials.TryGetValue(authType, out var credential) || credential == null) return null;
            if (ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials)) return CredentialCache.DefaultNetworkCredentials;
            return new NetworkCredential(credential.UserName, credential.Password, credential.Domain);
        }
    }
}

internal sealed class NetworkRouteResolver
{
    private static readonly string[] AuthenticationTypes = ["Negotiate", "NTLM", "Digest", "Basic", "SOCKS", "socks4", "socks4a", "socks5"];
    private readonly byte[] _fingerprintKey = RandomNumberGenerator.GetBytes(32);
    internal Func<IWebProxy> SystemProxy { get; init; } = static () => HttpClient.DefaultProxy;

    internal NetworkRoute Resolve(NetworkProxySnapshot snapshot)
    {
        if (snapshot.Mode == ProxyMode.None) return Direct(snapshot.RouteKind);
        try
        {
            Uri proxyUri;
            bool defaults = false;
            bool allDefaults = false;
            var values = new Dictionary<string, NetworkCredential?>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.Mode == ProxyMode.Custom)
            {
                proxyUri = NetworkProxyCodec.ParseUri(snapshot.CustomProxyUri?.AbsoluteUri);
                NetworkProxyCodec.ValidateCredentials(snapshot.Credentials, proxyUri);
                foreach (var type in AuthenticationTypes)
                    values[type] = snapshot.Credentials is { } pair ? new NetworkCredential(pair.UserName, pair.Password) : null;
            }
            else if (snapshot.Mode == ProxyMode.System)
            {
                var system = SystemProxy();
                if (system.IsBypassed(snapshot.Destination)) return Direct(ProxyRouteKind.DirectBySystemBypass);
                var originalUri = system.GetProxy(snapshot.Destination);
                if (originalUri == null || originalUri == snapshot.Destination) return Direct(ProxyRouteKind.DirectBySystemBypass);
                proxyUri = NetworkProxyCodec.ParseUri(originalUri.AbsoluteUri, allowUserInfo: true, system: true);
                var credentials = system.Credentials ?? CredentialCache.DefaultCredentials;
                defaults = ReferenceEquals(credentials, CredentialCache.DefaultCredentials) || ReferenceEquals(credentials, CredentialCache.DefaultNetworkCredentials);
                allDefaults = defaults;
                if (!defaults)
                {
                    foreach (var type in AuthenticationTypes)
                    {
                        // Use the original URI for the system callback, then bind the copy to the clean URI.
                        var value = credentials.GetCredential(originalUri, type);
                        if (ReferenceEquals(value, CredentialCache.DefaultNetworkCredentials)) defaults = true;
                        values[type] = value == null ? null : ReferenceEquals(value, CredentialCache.DefaultNetworkCredentials)
                            ? CredentialCache.DefaultNetworkCredentials : new NetworkCredential(value.UserName, value.Password, value.Domain);
                    }
                }
            }
            else throw NetworkProxyCodec.Unavailable();
            return new NetworkRoute
            {
                ProxyUri = proxyUri, Kind = ProxyRouteKind.Proxy, UsesDefaultCredentials = defaults,
                AllDefaultCredentials = allDefaults,
                Credentials = values, Identity = Fingerprint(proxyUri.AbsoluteUri, defaults, values)
            };
        }
        catch { throw NetworkProxyCodec.Unavailable(); }
    }

    private static NetworkRoute Direct(ProxyRouteKind kind) => new() { Kind = kind, Identity = "DIRECT" };

    private string Fingerprint(string proxy, bool defaults, Dictionary<string, NetworkCredential?> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            // BinaryWriter strings are length-prefixed; null and empty credentials have distinct encodings.
            writer.Write(proxy); writer.Write(defaults);
            foreach (var type in AuthenticationTypes)
            {
                writer.Write(type);
                values.TryGetValue(type, out var value);
                writer.Write(value != null);
                writer.Write(ReferenceEquals(value, CredentialCache.DefaultNetworkCredentials));
                if (value != null) { writer.Write(value.Domain); writer.Write(value.UserName); writer.Write(value.Password); }
            }
        }
        return Convert.ToHexString(HMACSHA256.HashData(_fingerprintKey, buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}

/// <summary>Internal socket boundary. Tests replace only connection establishment or owned leaf resources.</summary>
internal class NetworkTransportFactory
{
    internal virtual HttpMessageHandler Create(NetworkRoute route)
    {
        return new SocketsHttpHandler
        {
            Proxy = route.CreateProxy(), UseProxy = route.ProxyUri != null,
            DefaultProxyCredentials = route.UsesDefaultCredentials ? CredentialCache.DefaultCredentials : null,
            AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All
        };
    }
}
