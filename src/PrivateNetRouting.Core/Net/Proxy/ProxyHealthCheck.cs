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
    /// from the proxy (like "CONNECT tunnel in N ms" / "proxy responded: …" in the legacy scheme).
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
            var request = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, cts.Token);
            stopwatch.Stop();
            if (read == 0)
            {
                return HealthProbeResult.FailIn(stopwatch.ElapsedMilliseconds, "connection closed without response");
            }

            var response = Encoding.ASCII.GetString(buffer, 0, read);
            var statusLine = response.Split('\r')[0];
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
}
