using System.Net.Sockets;
using System.Text;

namespace PrivateNetRouting.Core.Net.Proxy;

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
        CancellationToken ct = default)
    {
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
            if (read == 0)
            {
                return false;
            }

            var response = Encoding.ASCII.GetString(buffer, 0, read);
            var statusLine = response.Split('\r')[0];
            return statusLine.StartsWith("HTTP/1.1 2", StringComparison.Ordinal)
                || statusLine.StartsWith("HTTP/1.0 2", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return false;
        }
    }
}
