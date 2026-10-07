using System.Net;

namespace Iris.Core.Network;

internal static class NetworkAddressClassifier
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

    internal static bool IsLocalAddress(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var host = uri.DnsSafeHost.TrimEnd('.');
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
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

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

}
