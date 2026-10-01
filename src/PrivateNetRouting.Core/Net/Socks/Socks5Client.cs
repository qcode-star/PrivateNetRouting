using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PrivateNetRouting.Core.Net.Socks;

/// <summary>
/// SOCKS5-клиент (RFC 1928) для userspace-апстрима: без аутентификации, дозвон только по IP
/// (IPv4, ATYP=1). Важно: имя в апстрим не передаём — его собственный резолв отравлен
/// hosts/NRPT-баном, резолв всегда делает вызывающая сторона.
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

    /// <summary>Устанавливает туннель до IP:port через апстрим и возвращает готовый сокет.</summary>
    public async Task<Socket> ConnectAsync(IPAddress destination, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException($"socks5: дозвон поддерживается только по IPv4, получено {destination}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_upstream, cts.Token);
            var stream = new NetworkStream(socket, ownsSocket: false);

            // greeting: VER=5, один метод, no-auth
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
            var method = new byte[2];
            await ReadExactAsync(stream, method, cts.Token);
            if (method[0] != 0x05 || method[1] != 0x00)
            {
                throw new IOException("socks5: апстрим не принял метод без аутентификации");
            }

            // CONNECT: VER CMD=1 RSV=0 ATYP=1 IPv4 порт
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
                throw new IOException($"socks5: connect отклонён (rep={header[1]})");
            }

            // связанный адрес пропускаем, важно только то, что запрос принят
            var boundLength = header[3] switch
            {
                0x01 => 4,
                0x03 => 1, // длина имени читается отдельно
                0x04 => 16,
                _ => throw new IOException($"socks5: неизвестный ATYP {header[3]}"),
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
                throw new IOException("socks5: апстрим закрыл соединение досрочно");
            }

            read += chunk;
        }
    }

    public static IPEndPoint ParseEndPoint(string hostPort)
    {
        var separator = hostPort.LastIndexOf(':');
        if (separator <= 0 || separator == hostPort.Length - 1)
        {
            throw new FormatException($"ожидался 'host:port', получено '{hostPort}'");
        }

        var host = hostPort[..separator];
        var port = int.Parse(hostPort[(separator + 1)..]);
        if (!IPAddress.TryParse(host, out var ip))
        {
            // системный резолв не используется: апстрим — всегда IP из конфига
            throw new FormatException($"socks5: апстрим должен быть IP-адресом, получено '{host}'");
        }

        return new IPEndPoint(ip, port);
    }
}
