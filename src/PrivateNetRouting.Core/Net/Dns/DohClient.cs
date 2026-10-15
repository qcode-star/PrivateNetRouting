using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// A single DoH query (RFC 8484, GET with a base64url dns parameter). HTTP/1.0: the response
/// body is delimited by Content-Length or by closure — we parse only the status and the body.
/// The transport dials the endpoint through the SOCKS5 upstream; https endpoints wrap the
/// tunnel in TLS (arbitrary tailscale configs carry https resolvers; plain http stays for the
/// tunnel-internal connectors).
/// </summary>
public static class DohClient
{
    /// <summary>Test seam: the harness serves self-signed certificates.</summary>
    internal static RemoteCertificateValidationCallback? TestCertificateValidation { get; set; }

    public static async Task<DnsAnswer> QueryAsync(
        Socks5Client socks,
        DohEndpoint endpoint,
        string name,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var socket = await socks.ConnectAsync(endpoint.Ip, endpoint.Port, timeout, cts.Token);
        try
        {
            using var _ = socket;
            using var transport = new NetworkStream(socket, ownsSocket: false);
            if (!endpoint.Secure)
            {
                return await QueryOverStreamAsync(transport, endpoint, name, cts.Token);
            }

            using var tls = new SslStream(transport, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.HostName ?? endpoint.Ip.ToString(),
                RemoteCertificateValidationCallback = TestCertificateValidation,
            }, cts.Token);
            return await QueryOverStreamAsync(tls, endpoint, name, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"doh {endpoint}: timeout after {timeout.TotalSeconds:F0}s");
        }
    }

    /// <summary>A DNS message is at most 64 KB; anything larger than this is not a DoH answer.</summary>
    private const int MaxResponseBytes = 128 * 1024;

    internal static async Task<DnsAnswer> QueryOverStreamAsync(
        Stream stream,
        DohEndpoint endpoint,
        string name,
        CancellationToken ct)
    {
        var query = DnsWire.BuildQuery(name);
        var dnsParam = Convert.ToBase64String(query)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // the Host header carries the port: a non-default one must be visible to the server
        var host = endpoint.HostName is null ? endpoint.ToString() : $"{endpoint.HostName}:{endpoint.Port}";
        var request = $"GET /dns-query?dns={dnsParam} HTTP/1.0\r\n" +
                      $"Host: {host}\r\n" +
                      "Accept: application/dns-message\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        // the end of the response is Content-Length when the server states it (a keep-alive
        // server never closes: reading until EOF would cost the whole timeout and mark the
        // endpoint dead), the closure otherwise; both are bounded
        var data = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var headerEnd = -1;
        long expectedTotal = -1;
        while (true)
        {
            var chunk = await stream.ReadAsync(buffer, ct);
            if (chunk == 0)
            {
                break;
            }

            data.Write(buffer, 0, chunk);
            if (data.Length > MaxResponseBytes)
            {
                throw new FormatException("doh: response too large");
            }

            if (headerEnd < 0)
            {
                headerEnd = data.GetBuffer().AsSpan(0, (int)data.Length).IndexOf("\r\n\r\n"u8);
                if (headerEnd >= 0
                    && ParseContentLength(Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd)) is { } length)
                {
                    if (length > MaxResponseBytes)
                    {
                        throw new FormatException("doh: response too large");
                    }

                    expectedTotal = headerEnd + 4 + length;
                }
            }

            if (expectedTotal >= 0 && data.Length >= expectedTotal)
            {
                break;
            }
        }

        var raw = data.ToArray();
        return ParseHttpResponse(expectedTotal >= 0 && raw.Length > expectedTotal ? raw[..(int)expectedTotal] : raw);
    }

    private static long? ParseContentLength(string head)
    {
        foreach (var line in head.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0
                && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line[(colon + 1)..].Trim(), out var length)
                && length >= 0)
            {
                return length;
            }
        }

        return null;
    }

    internal static DnsAnswer ParseHttpResponse(byte[] raw)
    {
        var separator = raw.AsSpan().IndexOf("\r\n\r\n"u8);
        if (separator < 0)
        {
            throw new FormatException("doh: response without header delimiter");
        }

        var head = Encoding.ASCII.GetString(raw, 0, separator);
        var statusLine = head.Split("\r\n")[0].Split(' ', 3);
        var statusCode = statusLine.Length >= 2 ? statusLine[1] : "?";
        if (!int.TryParse(statusCode, out var status) || status != 200)
        {
            throw new FormatException($"doh: http {statusCode}");
        }

        var body = raw[(separator + 4)..];
        var answer = DnsWire.ParseResponse(body);
        if (answer.Ips.Count == 0)
        {
            throw new DnsNegativeAnswerException("doh: no A record (NODATA)");
        }

        return answer;
    }
}
