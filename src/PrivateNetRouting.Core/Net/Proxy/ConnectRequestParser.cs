namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// Parsed CONNECT: the host is normalized (no trailing dot, lowercase). IsProbe — the request
/// carries the health-probe marker header: the proxy still serves it, but it is our own
/// liveness check, not client traffic, so it must not count as activity for the idle stopper.
/// </summary>
public sealed record ConnectRequest(string Host, int Port, bool IsProbe = false)
{
    /// <summary>Host:port as written in a CONNECT (an IPv6 literal in brackets).</summary>
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>
/// Parses the first line of a proxy request: "CONNECT host:port HTTP/x.y". Only CONNECT is
/// supported (https traffic); plain HTTP gets a 400 with a log entry.
/// </summary>
public static class ConnectRequestParser
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const int DefaultPort = 443;

    /// <summary>The header our own health probes put on their CONNECT (see <see cref="ProxyHealthCheck"/>).</summary>
    public const string ProbeHeaderName = "X-Pnr-Probe";

    public static bool TryParse(ReadOnlySpan<byte> header, out ConnectRequest? request, out string? error)
    {
        request = null;
        error = null;
        var lineEnd = header.IndexOf((byte)'\r');
        if (lineEnd < 0)
        {
            error = "no request line terminator";
            return false;
        }

        var line = System.Text.Encoding.Latin1.GetString(header[..lineEnd]);
        var parts = line.Split(' ', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            error = $"not a CONNECT request: {Truncate(line)}";
            return false;
        }

        var authority = parts[1];
        int port;
        string host;
        var ipv6Literal = authority.StartsWith('[');
        if (ipv6Literal)
        {
            // "[2001:db8::1]:443": the brackets keep the address colons apart from the port's
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                error = $"invalid host: {Truncate(authority)}";
                return false;
            }

            host = authority[1..close];
            var rest = authority[(close + 1)..];
            if (rest.Length == 0)
            {
                port = DefaultPort;
            }
            else if (rest[0] != ':' || !int.TryParse(rest[1..], out port) || port is < 1 or > 65535)
            {
                error = $"invalid port: {Truncate(authority)}";
                return false;
            }
        }
        else
        {
            var separator = authority.LastIndexOf(':');
            if (separator >= 0)
            {
                host = authority[..separator];
                if (!int.TryParse(authority[(separator + 1)..], out port) || port is < 1 or > 65535)
                {
                    error = $"invalid port: {Truncate(authority)}";
                    return false;
                }
            }
            else
            {
                host = authority;
                port = DefaultPort;
            }
        }

        if (host.Length == 0)
        {
            error = "empty host";
            return false;
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        if (!IsDialableHost(host, ipv6Literal))
        {
            error = $"invalid host: {Truncate(host)}";
            return false;
        }

        request = new ConnectRequest(host, port, HasProbeHeader(header[lineEnd..]));
        return true;
    }

    /// <summary>
    /// A bracketed IPv6 literal (no zone), an IPv4 literal, or a DNS name: non-empty labels of at
    /// most 63 bytes, 253 in all. Anything else ("a..b", a colon in a name) could only fail after
    /// the upstream dial was spent.
    /// </summary>
    private static bool IsDialableHost(string host, bool ipv6Literal)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        if (ipv6Literal)
        {
            return System.Net.IPAddress.TryParse(host, out var v6)
                && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && v6.ScopeId == 0;
        }

        if (System.Net.IPAddress.TryParse(host, out var literal))
        {
            return literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63)
            {
                return false;
            }
        }

        return !host.Contains('[') && !host.Contains(']') && !host.Contains(':');
    }

    /// <summary>Whether the header block (the part after the request line) holds the probe marker header.</summary>
    private static bool HasProbeHeader(ReadOnlySpan<byte> headers)
    {
        foreach (var raw in System.Text.Encoding.Latin1.GetString(headers).Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon > 0
                && raw[..colon].Trim().Equals(ProbeHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120];
}
