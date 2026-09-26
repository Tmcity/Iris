using System;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Iris.Core.Settings;

namespace Iris.Providers.Http;

/// <summary>
/// 动态代理，读取 ISettingsService.Current.Network 决定代理目标与直连规则。
/// 设置修改后无需重启即可生效。
/// </summary>
internal sealed partial class DynamicWebProxy : IWebProxy
{
    private static readonly IPNetwork[] LocalNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    private readonly ISettingsService _settings;
    private readonly ILogger<DynamicWebProxy> _logger;
    private int _warnedInvalidProxy;

    public DynamicWebProxy(ISettingsService settings, ILogger<DynamicWebProxy> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public ICredentials? Credentials
    {
        get
        {
            var network = _settings.Current.Network;
            if (network.ProxyMode == ProxyMode.System)
            {
                return HttpClient.DefaultProxy.Credentials ?? CredentialCache.DefaultCredentials;
            }
            return null;
        }
        set => throw new NotSupportedException();
    }

    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (IsBypassed(destination))
        {
            return null;
        }

        var network = _settings.Current.Network;
        return network.ProxyMode switch
        {
            ProxyMode.None => null,
            ProxyMode.System => HttpClient.DefaultProxy.GetProxy(destination),
            ProxyMode.Custom => GetCustomProxyUri(network.CustomProxyUrl),
            _ => null,
        };
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var network = _settings.Current.Network;
        if (network.ProxyMode == ProxyMode.None)
        {
            return true;
        }

        if (network.BypassProxyOnLocal && IsLocalAddress(host))
        {
            return true;
        }

        if (network.ProxyMode == ProxyMode.System)
        {
            return HttpClient.DefaultProxy.IsBypassed(host);
        }

        return false;
    }

    internal static bool IsLocalAddress(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var host = uri.DnsSafeHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            foreach (var network in LocalNetworks)
            {
                if (network.Contains(ip))
                {
                    return true;
                }
            }

            return false;
        }

        return !host.Contains('.');
    }

    private Uri? GetCustomProxyUri(string? customProxyUrl)
    {
        if (Uri.TryCreate(customProxyUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase)))
        {
            return uri;
        }

        if (Interlocked.CompareExchange(ref _warnedInvalidProxy, 1, 0) == 0)
        {
            LogInvalidCustomProxy(_logger);
        }

        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "自定义代理地址无效，已直连")]
    private static partial void LogInvalidCustomProxy(ILogger logger);
}
