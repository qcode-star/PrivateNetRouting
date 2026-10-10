using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PrivateNetRouting.Core.Net.Socks;

/// <summary>
/// SOCKS5 client (RFC 1928) for the upstream: no authentication or username/password
/// (RFC 1929). Two dial forms: by IPv4 (ATYP=1) — the tailscale sidecar's own resolution is
/// poisoned by the hosts/NRPT ban, so the caller always resolves first; and by name (ATYP=3) —
/// the socks5 mode passes the target name to the trusted external upstream, which resolves it
/// on its side.
/// </summary>
public sealed class Socks5Client
{
    private readonly EndPoint _upstream;
    private readonly string? _user;
    private readonly string? _password;

    public Socks5Client(string upstream, string? user = null, string? password = null)
    {
        _upstream = ParseEndPoint(upstream);
        (_user, _password) = NormalizeCredentials(user, password);
    }

    public Socks5Client(IPEndPoint upstream, string? user = null, string? password = null)
    {
        _upstream = upstream;
        (_user, _password) = NormalizeCredentials(user, password);
    }

    /// <summary>Credentials go together and only both; empty values mean no authentication.</summary>
    private static (string? User, string? Password) NormalizeCredentials(string? user, string? password)
    {
        return string.IsNullOrEmpty(user) != string.IsNullOrEmpty(password)
            ? throw new ArgumentException("socks5: upstream user and password must be set together")
            : (string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(password) ? null : password);
    }

    /// <summary>Establishes a tunnel to IP:port through the upstream and returns a ready socket.</summary>
    public Task<Socket> ConnectAsync(IPAddress destination, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException($"socks5: dial is IPv4-only, got {destination}");
        }

        // CONNECT: VER CMD=1 RSV=0 ATYP=1 IPv4 port
        var request = new byte[10];
        request[0] = 0x05;
        request[1] = 0x01;
        request[3] = 0x01;
        destination.TryWriteBytes(request.AsSpan(4, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), (ushort)port);
        return DialAsync(request, timeout, ct);
    }

    /// <summary>
    /// Establishes a tunnel to host:port, passing the name to the upstream (ATYP=3) — its
    /// resolver answers. A name that is an IP literal still dials by ATYP=1.
    /// </summary>
    public Task<Socket> ConnectAsync(string hostName, int port, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        if (IPAddress.TryParse(hostName, out var literal))
        {
            return ConnectAsync(literal, port, timeout, ct);
        }

        var nameLength = Encoding.UTF8.GetByteCount(hostName);
        if (nameLength is < 1 or > 255)
        {
            throw new ArgumentException($"socks5: hostname must be 1..255 bytes, got {nameLength}", nameof(hostName));
        }

        // CONNECT: VER CMD=1 RSV=0 ATYP=3 name-len name port
        var request = new byte[7 + nameLength];
        request[0] = 0x05;
        request[1] = 0x01;
        request[3] = 0x03;
        request[4] = (byte)nameLength;
        Encoding.UTF8.GetBytes(hostName, request.AsSpan(5));
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(5 + nameLength, 2), (ushort)port);
        return DialAsync(request, timeout, ct);
    }

    /// <summary>Negotiates the upstream, sends the prepared CONNECT request, returns the tunnel socket.</summary>
    private async Task<Socket> DialAsync(byte[] request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_upstream, cts.Token);
            var stream = new NetworkStream(socket, ownsSocket: false);

            // greeting: VER=5, one method — no-auth, or username/password when configured
            await stream.WriteAsync(_user is null ? new byte[] { 0x05, 0x01, 0x00 } : new byte[] { 0x05, 0x01, 0x02 }, cts.Token);
            var method = new byte[2];
            await ReadExactAsync(stream, method, cts.Token);
            var expectedMethod = _user is null ? (byte)0x00 : (byte)0x02;
            if (method[0] != 0x05 || method[1] != expectedMethod)
            {
                throw new IOException(_user is null
                    ? "socks5: upstream rejected the no-auth method"
                    : "socks5: upstream rejected the username/password method");
            }

            if (_user is not null)
            {
                await AuthenticateAsync(stream, _user, _password!, cts.Token);
            }

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

    /// <summary>RFC 1929 username/password subnegotiation (VER=1).</summary>
    private static async Task AuthenticateAsync(NetworkStream stream, string user, string password, CancellationToken ct)
    {
        var userBytes = Encoding.UTF8.GetBytes(user);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        if (userBytes.Length is < 1 or > 255 || passwordBytes.Length is < 1 or > 255)
        {
            throw new InvalidOperationException("socks5: username/password must be 1..255 bytes");
        }

        var request = new byte[3 + userBytes.Length + passwordBytes.Length];
        request[0] = 0x01; // subnegotiation version
        request[1] = (byte)userBytes.Length;
        userBytes.CopyTo(request, 2);
        request[2 + userBytes.Length] = (byte)passwordBytes.Length;
        passwordBytes.CopyTo(request, 3 + userBytes.Length);
        await stream.WriteAsync(request, ct);

        var answer = new byte[2];
        await ReadExactAsync(stream, answer, ct);
        if (answer[0] != 0x01 || answer[1] != 0x00)
        {
            throw new IOException("socks5: upstream rejected the credentials");
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
