using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>Result of a detailed probe: success, roundtrip time and the first response line (or the failure reason).</summary>
public sealed record HealthProbeResult(bool Ok, long Milliseconds, string Detail)
{
    public static HealthProbeResult OkIn(long milliseconds, string statusLine) => new(true, milliseconds, statusLine);

    public static HealthProbeResult FailIn(long milliseconds, string detail) => new(false, milliseconds, detail);
}

/// <summary>
/// Liveness of the whole chain with a single CONNECT roundtrip through the proxy under test:
/// catches the death of any link (daemon, keeper, DoH, connector). Success — a 2xx response to
/// CONNECT.
/// </summary>
public static class ProxyHealthCheck
{
    public static async Task<bool> CheckAsync(
        string proxyHost,
        int proxyPort,
        string targetHost,
        int targetPort,
        TimeSpan timeout,
        CancellationToken ct = default) =>
        (await ProbeAsync(proxyHost, proxyPort, targetHost, targetPort, timeout, ct)).Ok;

    /// <summary>
    /// The same probe with details for diagnostics: the roundtrip time and the first response line
    /// from the proxy (like "CONNECT tunnel in N ms" / "proxy responded: …").
    /// </summary>
    public static async Task<HealthProbeResult> ProbeAsync(
        string proxyHost,
        int proxyPort,
        string targetHost,
        int targetPort,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(proxyHost, proxyPort, cts.Token);
            var stream = client.GetStream();
            // the marker header tells our own proxy that this is a liveness probe, not client
            // traffic: otherwise every status poll would refresh the activity marker and the
            // idle stopper would never see an idle chain
            // HTTP/1.1 requires Host; a strict upstream proxy (the http mode) answers 400 without it
            // the target is bracketed when it is an IPv6 literal (ConnectRequest.Authority) — an
            // unbracketed one is rejected by the proxy as an ambiguous port
            var target = ConnectRequest.Authority(targetHost, targetPort);
            var request = $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n" +
                          $"{ConnectRequestParser.ProbeHeaderName}: 1\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

            var statusLine = await ReadStatusLineAsync(stream, cts.Token);
            stopwatch.Stop();
            if (statusLine is null)
            {
                return HealthProbeResult.FailIn(stopwatch.ElapsedMilliseconds, "connection closed without response");
            }

            return statusLine.StartsWith("HTTP/1.1 2", StringComparison.Ordinal)
                || statusLine.StartsWith("HTTP/1.0 2", StringComparison.Ordinal)
                ? HealthProbeResult.OkIn(stopwatch.ElapsedMilliseconds, statusLine)
                : HealthProbeResult.FailIn(stopwatch.ElapsedMilliseconds, statusLine);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            stopwatch.Stop();
            return HealthProbeResult.FailIn(stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// The first response line, however the segments split it: reads until the line terminator
    /// (or a sane limit); null — the peer closed before sending anything.
    /// </summary>
    private static async Task<string?> ReadStatusLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (buffer.AsSpan(0, total).IndexOfAny((byte)'\r', (byte)'\n') >= 0)
            {
                break;
            }
        }

        if (total == 0)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
