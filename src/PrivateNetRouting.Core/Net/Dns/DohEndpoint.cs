using System.Net;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// DoH endpoint of the form http(s)://IP:PORT/dns-query (RFC 8484). Plain HTTP is the shape of
/// the tailnet's own connectors (the transport is already encrypted by the tunnel); https comes
/// from arbitrary tailscale DNS configs and is dialed through the same tunnel with TLS on top.
/// The host is always an IP — the request to it goes via SOCKS5 without system resolution;
/// HostName keeps the original name of a https endpoint found by discovery and is used for the
/// TLS SNI and the certificate check.
/// </summary>
public sealed record DohEndpoint(IPAddress Ip, int Port, bool Secure = false, string? HostName = null)
{
    public static bool TryParse(string value, out DohEndpoint? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.AbsolutePath != "/dns-query")
        {
            return false;
        }

        var host = uri.Host.StartsWith('[') ? uri.Host.Trim('[', ']') : uri.Host;
        if (!IPAddress.TryParse(host, out var ip) || uri.Port < 1)
        {
            return false;
        }

        endpoint = new DohEndpoint(ip, uri.Port, Secure: uri.Scheme == Uri.UriSchemeHttps);
        return true;
    }

    public override string ToString() =>
        HostName is null ? $"{Ip}:{Port}" : $"{HostName} ({Ip}:{Port})";
}
