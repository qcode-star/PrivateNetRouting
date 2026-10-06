using System.Net;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// DoH endpoint of the form http://IP:PORT/dns-query (RFC 8484, plain HTTP: the transport is
/// encrypted by the upstream tunnel). The host is always an IP — the request to it goes via
/// SOCKS5 without system resolution.
/// </summary>
public sealed record DohEndpoint(IPAddress Ip, int Port)
{
    public static bool TryParse(string value, out DohEndpoint? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/dns-query")
        {
            return false;
        }

        var host = uri.Host.StartsWith('[') ? uri.Host.Trim('[', ']') : uri.Host;
        if (!IPAddress.TryParse(host, out var ip) || uri.Port < 1)
        {
            return false;
        }

        endpoint = new DohEndpoint(ip, uri.Port);
        return true;
    }

    public override string ToString() => $"{Ip}:{Port}";
}
