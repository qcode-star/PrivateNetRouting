using System.Buffers.Binary;
using System.Net;
using System.Text;
using PrivateNetRouting.Core.Net.Dns;

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
        Assert.Throws<FormatException>(() => DohClient.ParseHttpResponse(raw));
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
}
