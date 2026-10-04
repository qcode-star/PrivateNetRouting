using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>Результат детальной пробы: успех, время roundtrip'а и первая строка ответа (или причина отказа).</summary>
public sealed record HealthProbeResult(bool Ok, long Milliseconds, string Detail)
{
    public static HealthProbeResult OkIn(long milliseconds, string statusLine) => new(true, milliseconds, statusLine);

    public static HealthProbeResult FailIn(long milliseconds, string detail) => new(false, milliseconds, detail);
}

/// <summary>
/// Живость всей цепочки одним CONNECT-roundtrip'ом через проверяемый прокси: ловит смерть
/// любого звена (демон, кипер, DoH, коннектор). Успех — ответ 2xx на CONNECT.
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
    /// Та же проба с деталями для диагностики: время roundtrip'а и первая строка ответа
    /// прокси (как «CONNECT-туннель за N мс» / «прокси ответил: …» в прежней схеме).
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
