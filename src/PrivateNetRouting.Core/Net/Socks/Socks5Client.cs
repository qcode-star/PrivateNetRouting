using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PrivateNetRouting.Core.Net.Socks;

/// <summary>
/// SOCKS5 client (RFC 1928) for the userspace upstream: no authentication, dialing by IP only
/// (IPv4, ATYP=1). Important: we do not pass the name to the upstream — its own resolution is
/// poisoned by the hosts/NRPT ban, resolution is always done by the caller.
/// </summary>
public sealed class Socks5Client
{
    private readonly EndPoint _upstream;

    public Socks5Client(string upstream)
    {
        _upstream = ParseEndPoint(upstream);
    }

    public Socks5Client(IPEndPoint upstream)
    {
        _upstream = upstream;
    }

    /// <summary>Establishes a tunnel to IP:port through the upstream and returns a ready socket.</summary>
    public async Task<Socket> ConnectAsync(IPAddress destination, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException($"socks5: dial is IPv4-only, got {destination}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_upstream, cts.Token);
            var stream = new NetworkStream(socket, ownsSocket: false);

            // greeting: VER=5, one method, no-auth
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
            var method = new byte[2];
            await ReadExactAsync(stream, method, cts.Token);
            if (method[0] != 0x05 || method[1] != 0x00)
            {
                throw new IOException("socks5: upstream rejected the no-auth method");
            }

            // CONNECT: VER CMD=1 RSV=0 ATYP=1 IPv4 port
            var request = new byte[10];
            request[0] = 0x05;
            request[1] = 0x01;
            request[3] = 0x01;
            destination.TryWriteBytes(request.AsSpan(4, 4), out _);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), (ushort)port);
            await stream.WriteAsync(request, cts.Token);

            var header = new byte[4]; // VER REP RSV ATYP
            await ReadExactAsync(stream, header, cts.Token);
            if (header[1] != 0x00)
            {
                throw new IOException($"socks5: connect rejected (rep={header[1]})");
            }

            // skip the bound address, only the fact that the request was accepted matters
            var boundLength = header[3] switch
            {
                0x01 => 4,
                0x03 => 1, // the name length is read separately
                0x04 => 16,
                _ => throw new IOException($"socks5: unknown ATYP {header[3]}"),
            };
            if (header[3] == 0x03)
            {
                var len = new byte[1];
                await ReadExactAsync(stream, len, cts.Token);
                boundLength = len[0];
            }

            var tail = new byte[boundLength + 2];
            await ReadExactAsync(stream, tail, cts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (chunk == 0)
            {
                throw new IOException("socks5: upstream closed the connection early");
            }

            read += chunk;
        }
    }

    public static IPEndPoint ParseEndPoint(string hostPort)
    {
        var separator = hostPort.LastIndexOf(':');
        if (separator <= 0 || separator == hostPort.Length - 1)
        {
            throw new FormatException($"expected 'host:port', got '{hostPort}'");
        }

        var host = hostPort[..separator];
        var port = int.Parse(hostPort[(separator + 1)..]);
        if (!IPAddress.TryParse(host, out var ip))
        {
            // system resolution is not used: the upstream is always an IP from the config
            throw new FormatException($"socks5: upstream must be an IP address, got '{host}'");
        }

        return new IPEndPoint(ip, port);
    }
}
