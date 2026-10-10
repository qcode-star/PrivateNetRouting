using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

        var (seenTarget, seenPort, seenAtyp) = await connectTask;
        Assert.Equal("10.1.2.3", seenTarget);
        Assert.Equal(8443, seenPort);
        Assert.Equal(0x01, seenAtyp);
    }

    [Fact]
    public async Task DialsByNamePassingItToTheUpstream()
    {
        var (upstream, connectTask) = await StartFakeUpstreamAsync();
        var client = new Socks5Client(upstream);

        var socket = await client.ConnectAsync("target.example.internal", 8443, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var _ = socket;

        var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync("ping"u8.ToArray());
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);
        Assert.Equal("pong"u8.ToArray(), buffer[..read]);

        var (seenTarget, seenPort, seenAtyp) = await connectTask;
        Assert.Equal(0x03, seenAtyp);
        Assert.Equal("target.example.internal", seenTarget);
        Assert.Equal(8443, seenPort);
    }

    [Fact]
    public async Task NameThatIsAnIpLiteralDialsByIp()
    {
        var (upstream, connectTask) = await StartFakeUpstreamAsync();
        var client = new Socks5Client(upstream);

        using var socket = await client.ConnectAsync("10.1.2.3", 8443, TimeSpan.FromSeconds(5), CancellationToken.None);

        var (seenTarget, _, seenAtyp) = await connectTask;
        Assert.Equal(0x01, seenAtyp);
        Assert.Equal("10.1.2.3", seenTarget);
    }

    [Fact]
    public async Task RejectsNamesOver255Bytes()
    {
        var client = new Socks5Client("127.0.0.1:1080");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ConnectAsync(new string('a', 256), 443, TimeSpan.FromSeconds(1), CancellationToken.None));
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
    public async Task AuthenticatesAndTunnelsWithUsernameAndPassword()
    {
        var (upstream, connectTask) = await StartFakeUpstreamAsync(user: "alice", password: "s3cret");
        var client = new Socks5Client(upstream, "alice", "s3cret");

        var socket = await client.ConnectAsync(IPAddress.Parse("10.1.2.3"), 8443, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var _ = socket;

        var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync("ping"u8.ToArray());
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer);
        Assert.Equal("pong"u8.ToArray(), buffer[..read]);

        var (seenTarget, seenPort, seenAtyp) = await connectTask;
        Assert.Equal("10.1.2.3", seenTarget);
        Assert.Equal(8443, seenPort);
        Assert.Equal(0x01, seenAtyp);
    }

    [Fact]
    public async Task ThrowsWhenUpstreamRejectsTheCredentials()
    {
        var (upstream, _) = await StartFakeUpstreamAsync(user: "alice", password: "s3cret");
        var client = new Socks5Client(upstream, "alice", "wrong");

        await Assert.ThrowsAsync<IOException>(() =>
            client.ConnectAsync(IPAddress.Loopback, 443, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task ThrowsWhenUpstreamRefusesUsernamePassword()
    {
        var (upstream, _) = await StartFakeUpstreamAsync(refuseAuth: true);
        var client = new Socks5Client(upstream, "alice", "s3cret");

        await Assert.ThrowsAsync<IOException>(() =>
            client.ConnectAsync(IPAddress.Loopback, 443, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public void ConstructorRequiresUserAndPasswordTogether()
    {
        Assert.Throws<ArgumentException>(() => new Socks5Client("127.0.0.1:1080", password: "x"));
        Assert.Throws<ArgumentException>(() => new Socks5Client("127.0.0.1:1080", user: "alice"));
        _ = new Socks5Client("127.0.0.1:1080"); // no credentials — fine
        _ = new Socks5Client("127.0.0.1:1080", "alice", "s3cret"); // both — fine
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

    /// <summary>
    /// Fake SOCKS5 upstream: answers the handshake (RFC 1929 user/pass subnegotiation when
    /// credentials are configured; the tunnel only opens with the exact pair), reads a CONNECT
    /// request of any address type and replies "pong".
    /// </summary>
    private static async Task<(IPEndPoint endpoint, Task<(string Target, int Port, byte Atyp)> seen)> StartFakeUpstreamAsync(
        bool refuseAuth = false,
        string? user = null,
        string? password = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var tcs = new TaskCompletionSource<(string, int, byte)>();

        var accept = Task.Run(async () =>
        {
            using var session = await listener.AcceptTcpClientAsync();
            var stream = session.GetStream();

            var greeting = new byte[3];
            await ReadExactAsync(stream, greeting);
            if (refuseAuth)
            {
                await stream.WriteAsync(new byte[] { 5, 0xFF });
                tcs.SetResult(("none", 0, 0));
                return;
            }

            if (user is null)
            {
                await stream.WriteAsync(new byte[] { 5, 0 });
            }
            else
            {
                await stream.WriteAsync(new byte[] { 5, 2 });
                var subnegotiation = new byte[2]; // VER ULEN
                await ReadExactAsync(stream, subnegotiation);
                if (subnegotiation[0] != 1)
                {
                    return;
                }

                var uname = new byte[subnegotiation[1]];
                var plen = new byte[1];
                await ReadExactAsync(stream, uname);
                await ReadExactAsync(stream, plen);
                var passwd = new byte[plen[0]];
                await ReadExactAsync(stream, passwd);

                var ok = Encoding.UTF8.GetString(uname) == user
                    && Encoding.UTF8.GetString(passwd) == password;
                await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 });
                if (!ok)
                {
                    return;
                }
            }

            var header = new byte[4]; // VER CMD RSV ATYP
            await ReadExactAsync(stream, header);
            byte[] address;
            if (header[3] == 0x03)
            {
                var len = new byte[1];
                await ReadExactAsync(stream, len);
                address = new byte[len[0]];
            }
            else
            {
                address = new byte[header[3] switch { 0x01 => 4, 0x04 => 16, _ => throw new IOException($"fake upstream: unknown ATYP {header[3]}") }];
            }

            await ReadExactAsync(stream, address);
            var portBytes = new byte[2];
            await ReadExactAsync(stream, portBytes);
            var target = header[3] switch
            {
                0x01 => new IPAddress(address).ToString(),
                0x03 => Encoding.UTF8.GetString(address),
                _ => "ipv6",
            };
            var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
            tcs.SetResult((target, port, header[3]));

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
