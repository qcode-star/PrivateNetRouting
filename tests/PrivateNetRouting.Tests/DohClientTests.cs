using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Socks;

namespace PrivateNetRouting.Tests;

public class DohClientTests
{
    [Fact]
    public void ParsesSuccessResponse()
    {
        var raw = Head(200).Concat(BuildResponse("1.2.3.4", ttl: 240)).ToArray();

        var answer = DohClient.ParseHttpResponse(raw);

        Assert.Single(answer.Ips);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), answer.Ips[0]);
        Assert.Equal(240u, answer.MinTtl);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(204)]
    public void ThrowsOnNon200(int status)
    {
        var raw = Head(status).Concat(BuildResponse("1.2.3.4", ttl: 60)).ToArray();
        Assert.Throws<FormatException>(() => DohClient.ParseHttpResponse(raw));
    }

    [Fact]
    public void ThrowsWhenAnswerEmpty()
    {
        var raw = Head(200).Concat(BuildResponse(ip: null, ttl: 60)).ToArray();
        Assert.Throws<DnsNegativeAnswerException>(() => DohClient.ParseHttpResponse(raw));
    }

    [Fact]
    public void ThrowsWithoutHeaderSeparator()
    {
        Assert.Throws<FormatException>(() => DohClient.ParseHttpResponse("HTTP/1.0 200 OK"u8.ToArray()));
    }

    private static byte[] Head(int status) =>
        Encoding.ASCII.GetBytes($"HTTP/1.0 {status} X\r\nContent-Type: application/dns-message\r\n\r\n");

    /// <summary>DNS response to the probe.example question: zero or one A record.</summary>
    private static byte[] BuildResponse(string? ip, uint ttl)
    {
        var writer = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[..2], 0xABCD);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x8180); // QR=1, rcode=0
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)(ip is null ? 0 : 1));
        writer.Write(header);

        WriteName(writer, "probe.example");
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[..2], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        writer.Write(tail);

        if (ip is not null)
        {
            WriteName(writer, "probe.example");
            Span<byte> fixedPart = stackalloc byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[..2], 1); // A
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..4], 1); // IN
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..8], ttl);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..], 4);
            writer.Write(fixedPart);
            writer.Write(IPAddress.Parse(ip).GetAddressBytes());
        }

        return writer.ToArray();
    }

    private static void WriteName(MemoryStream writer, string name)
    {
        foreach (var label in name.Split('.'))
        {
            writer.WriteByte((byte)label.Length);
            writer.Write(Encoding.ASCII.GetBytes(label));
        }

        writer.WriteByte(0);
    }

    [Fact]
    public async Task QueriesHttpsEndpointThroughSocksRelay()
    {
        DohClient.TestCertificateValidation = (_, _, _, _) => true;
        try
        {
            using var relay = await StartSocksRelayToAsync();
            var socks = new Socks5Client($"127.0.0.1:{relay.Port}");
            var endpoint = new DohEndpoint(
                IPAddress.Loopback, relay.TargetPort, Secure: true, HostName: "doh.example.test");

            var answer = await DohClient.QueryAsync(
                socks, endpoint, "api.example.com", TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal([IPAddress.Parse("203.0.113.77")], answer.Ips);
            Assert.Equal("doh.example.test", await relay.SeenSni); // the SNI carried the name
        }
        finally
        {
            DohClient.TestCertificateValidation = null; // the seam stays off outside the test
        }
    }

    /// <summary>
    /// Fake SOCKS5 upstream that relays every CONNECT to a fresh local TLS DoH server
    /// (self-signed certificate, answers every query with one A record, reports the SNI).
    /// </summary>
    private static async Task<TlsRelay> StartSocksRelayToAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=doh.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // an ephemeral in-memory key is refused by the Windows TLS server — re-import through PFX
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        var dohListener = new TcpListener(IPAddress.Loopback, 0);
        dohListener.Start();
        var seenSni = new TaskCompletionSource<string>();
        var targetPort = ((IPEndPoint)dohListener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            TcpClient client;
            try
            {
                client = await dohListener.AcceptTcpClientAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using var _ = client;
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(certificate);
            seenSni.TrySetResult(tls.TargetHostName);

            var buffer = new byte[8192];
            var data = new MemoryStream();
            while (!Encoding.ASCII.GetString(data.GetBuffer(), 0, (int)data.Length).Contains("\r\n\r\n"))
            {
                var read = await tls.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                data.Write(buffer, 0, read);
            }

            var requestText = Encoding.ASCII.GetString(data.ToArray());
            var param = requestText.Split("dns=")[1].Split(' ')[0].TrimEnd('\r', '\n');
            var base64 = param.Replace('-', '+').Replace('_', '/') + new string('=', (4 - param.Length % 4) % 4);
            var response = BuildTlsResponse(Convert.FromBase64String(base64), IPAddress.Parse("203.0.113.77"));
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: {response.Length}\r\n\r\n");
            await tls.WriteAsync(head);
            await tls.WriteAsync(response);
        });

        var relayListener = new TcpListener(IPAddress.Loopback, 0);
        relayListener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient session;
                try
                {
                    session = await relayListener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using var _ = session;
                    var stream = session.GetStream();
                    var greeting = new byte[3];
                    if (!await TryReadExactAsync(stream, greeting))
                    {
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 5, 0 });
                    var header = new byte[4];
                    if (!await TryReadExactAsync(stream, header) || header[3] != 0x01)
                    {
                        return;
                    }

                    var address = new byte[4];
                    var portBytes = new byte[2];
                    if (!await TryReadExactAsync(stream, address) || !await TryReadExactAsync(stream, portBytes))
                    {
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    try
                    {
                        using var target = new TcpClient();
                        await target.ConnectAsync(
                            new IPAddress(address), BinaryPrimitives.ReadUInt16BigEndian(portBytes));
                        var targetStream = target.GetStream();
                        await Task.WhenAll(
                            RelayAsync(stream, targetStream, "c2s"),
                            RelayAsync(targetStream, stream, "s2c"));
                    }
                    catch (SocketException)
                    {
                    }
                });
            }
        });

        var port = ((IPEndPoint)relayListener.LocalEndpoint).Port;
        return new TlsRelay(relayListener, port, targetPort, certificate, seenSni.Task);
    }

    /// <summary>DNS response: the query's question plus one A record (compression pointer to it).</summary>
    private static byte[] BuildTlsResponse(byte[] query, IPAddress ip)
    {
        var writer = new MemoryStream();
        writer.Write(query, 0, 2); // the ID of the query
        writer.Write([0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]); // QR|RD|RA, qd=1, an=1
        writer.Write(query, 12, query.Length - 12); // the question
        writer.Write([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04]); // ptr, A, IN, ttl 60, rdlen 4
        writer.Write(ip.GetAddressBytes());
        return writer.ToArray();
    }

    private static async Task RelayAsync(NetworkStream from, NetworkStream to, string tag)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (IOException)
        {
        }

        // propagate the EOF the way the production pump does — without this the peer's
        // read-until-close hangs forever
        try
        {
            to.Socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
    }

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read));
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private sealed class TlsRelay(
        TcpListener listener,
        int port,
        int targetPort,
        X509Certificate2 certificate,
        Task<string> seenSni) : IDisposable
    {
        public int Port { get; } = port;

        public int TargetPort { get; } = targetPort;

        public Task<string> SeenSni { get; } = seenSni;

        public void Dispose()
        {
            listener.Stop();
            certificate.Dispose();
        }
    }
}
