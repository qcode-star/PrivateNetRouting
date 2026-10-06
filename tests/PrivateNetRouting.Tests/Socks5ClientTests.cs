using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Tests;

public class Socks5ClientTests
{
    [Fact]
    public async Task HandshakeAndTunnelThroughFakeUpstream()
    {
        var (upstream, connectTask) = await StartFakeUpstreamAsync(refuseAuth: false);
        var client = new Socks5Client(upstream);

        var socket = await client.ConnectAsync(IPAddress.Parse("10.1.2.3"), 8443, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var _ = socket;

        var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync("ping"u8.ToArray());
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);
        Assert.Equal("pong"u8.ToArray(), buffer[..read]);

        var (seenIp, seenPort) = await connectTask;
        Assert.Equal("10.1.2.3", seenIp);
        Assert.Equal(8443, seenPort);
    }

    [Fact]
    public async Task ThrowsWhenUpstreamRefusesNoAuth()
    {
        var (upstream, _) = await StartFakeUpstreamAsync(refuseAuth: true);
        var client = new Socks5Client(upstream);

        await Assert.ThrowsAsync<IOException>(() =>
            client.ConnectAsync(IPAddress.Loopback, 443, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task ThrowsOnTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        _ = Task.Run(async () =>
        {
            using var silent = await listener.AcceptTcpClientAsync();
            await Task.Delay(TimeSpan.FromSeconds(10)); // stay silent: the client should time out
        });

        var client = new Socks5Client(endpoint);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConnectAsync(IPAddress.Loopback, 443, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        listener.Stop();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    public void ParseEndPointAcceptsIp(string value)
    {
        var endpoint = Socks5Client.ParseEndPoint($"{value}:1055");
        Assert.Equal(IPAddress.Parse(value), endpoint.Address);
        Assert.Equal(1055, endpoint.Port);
    }

    [Theory]
    [InlineData("no-port")]
    [InlineData(":1055")]
    [InlineData("example.com:1055")]
    [InlineData("127.0.0.1:")]
    public void ParseEndPointRejects(string value)
    {
        Assert.Throws<FormatException>(() => Socks5Client.ParseEndPoint(value));
    }

    /// <summary>Fake SOCKS5 upstream: answers the handshake and replies "pong" to "ping".</summary>
    private static async Task<(IPEndPoint endpoint, Task<(string Ip, int Port)> seen)> StartFakeUpstreamAsync(bool refuseAuth)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var tcs = new TaskCompletionSource<(string, int)>();

        var accept = Task.Run(async () =>
        {
            using var session = await listener.AcceptTcpClientAsync();
            var stream = session.GetStream();

            var greeting = new byte[3];
            await ReadExactAsync(stream, greeting);
            await stream.WriteAsync(refuseAuth ? new byte[] { 5, 0xFF } : new byte[] { 5, 0 });
            if (refuseAuth)
            {
                tcs.SetResult(("none", 0));
                return;
            }

            var request = new byte[10];
            await ReadExactAsync(stream, request);
            var ip = new IPAddress(request[4..8]).ToString();
            var port = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8, 2));
            tcs.SetResult((ip, port));

            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // REP=0, ATYP=IPv4, zeros

            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer);
            if (read == 4)
            {
                await stream.WriteAsync("pong"u8.ToArray());
            }
        });

        return (endpoint, tcs.Task);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read));
            if (chunk == 0)
            {
                throw new IOException("fake upstream closed prematurely");
            }

            read += chunk;
        }
    }
}
