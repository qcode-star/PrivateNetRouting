using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateNetRouting.Core.Net.Proxy;

namespace PrivateNetRouting.Tests;

public class ProxyHealthCheckTests
{
    /// <summary>
    /// A strict HTTP/1.1 proxy refuses a CONNECT without Host, and a response line may arrive in
    /// several segments: the probe sends Host and reads the whole status line.
    /// </summary>
    [Fact]
    public async Task SendsHostAndReadsAStatusLineSplitAcrossSegments()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var seenRequest = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using var session = await listener.AcceptTcpClientAsync();
            var stream = session.GetStream();
            var data = new MemoryStream();
            var buffer = new byte[1024];
            while (!Encoding.ASCII.GetString(data.ToArray()).Contains("\r\n\r\n"))
            {
                data.Write(buffer, 0, await stream.ReadAsync(buffer));
            }

            var request = Encoding.ASCII.GetString(data.ToArray());
            seenRequest.TrySetResult(request);
            var strictOk = request.Contains("\r\nHost: health.example:443\r\n", StringComparison.Ordinal);
            session.NoDelay = true;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(strictOk ? "HTTP/1.1 20" : "HTTP/1.1 40"));
            await Task.Delay(150); // the rest of the line comes in a later segment
            await stream.WriteAsync(Encoding.ASCII.GetBytes(strictOk ? "0 Connection Established\r\n\r\n" : "0 Bad Request\r\n\r\n"));
        });

        var result = await ProxyHealthCheck.ProbeAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "health.example", 443, TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("HTTP/1.1 200 Connection Established", result.Detail);
        Assert.Contains("CONNECT health.example:443 HTTP/1.1", await seenRequest.Task);
    }

    /// <summary>An IPv6 health target rides the CONNECT in brackets: unbracketed, the proxy reads
    /// the address's colons as a port separator and rejects the request.</summary>
    [Fact]
    public async Task BracketsAnIpv6Target()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var seenRequest = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using var session = await listener.AcceptTcpClientAsync();
            var stream = session.GetStream();
            var data = new MemoryStream();
            var buffer = new byte[1024];
            while (!Encoding.ASCII.GetString(data.ToArray()).Contains("\r\n\r\n"))
            {
                data.Write(buffer, 0, await stream.ReadAsync(buffer));
            }

            seenRequest.TrySetResult(Encoding.ASCII.GetString(data.ToArray()));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
        });

        var result = await ProxyHealthCheck.ProbeAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "2001:db8::1", 443, TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.True(result.Ok, result.Detail);
        Assert.Contains("CONNECT [2001:db8::1]:443 HTTP/1.1", await seenRequest.Task);
    }

    [Fact]
    public async Task ReportsANonSuccessStatusLine()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var session = await listener.AcceptTcpClientAsync();
            var stream = session.GetStream();
            _ = await stream.ReadAsync(new byte[1024]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"));
        });

        var result = await ProxyHealthCheck.ProbeAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "health.example", 443, TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.False(result.Ok);
        Assert.Equal("HTTP/1.1 502 Bad Gateway", result.Detail);
    }

    [Fact]
    public async Task ReportsAConnectionClosedWithoutAnyResponse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var session = await listener.AcceptTcpClientAsync();
            _ = await session.GetStream().ReadAsync(new byte[1024]);
        });

        var result = await ProxyHealthCheck.ProbeAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "health.example", 443, TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.False(result.Ok);
        Assert.Contains("closed without response", result.Detail);
    }
}
