using System.Net;
using System.Net.Sockets;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// Which CONNECT targets the proxy refuses outright. A tunnel to a loopback or link-local address
/// does not reach this machine: it reaches the loopback of the upstream's host (SOCKS5/HTTP
/// upstream) or its metadata/management addresses — an SSRF into whatever listens there, open to
/// any local process that can talk to the proxy. No client of the proxy means such a target.
/// Private ranges and the tailnet stay reachable (that is what the chain is for).
/// </summary>
public static class ConnectTargetPolicy
{
    /// <summary>Whether a CONNECT to this host (an IP literal or a name, normalized as the parser yields it) is refused.</summary>
    public static bool IsForbidden(string host)
    {
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsForbidden(address);
    }

    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            // 169.254.0.0/16: link-local, incl. the cloud metadata address; 0.0.0.0/8: "this host"
            AddressFamily.InterNetwork => address.GetAddressBytes() is [169, 254, ..] or [0, ..],
            // fe80::/10: link-local
            AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal,
            _ => false,
        };
    }
}
