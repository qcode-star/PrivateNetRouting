using System.Buffers.Binary;
using System.Net;
using PrivateNetRouting.Core.Net.Dns;

namespace PrivateNetRouting.Tests;

public class DnsWireTests
{
    [Fact]
    public void BuildQueryHasHeaderLabelsAndTail()
    {
        var query = DnsWire.BuildQuery("api.example.com");
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(query, offset, 2));

        Assert.Equal(0xABCD, U16(0));
        Assert.Equal(0x0100, U16(2));  // RD
        Assert.Equal(1, U16(4));       // QDCOUNT
        // question: 13 name characters + 3 label-length bytes + root zero
        Assert.Equal(13 + 3 + 1, query.Length - 12 - 4);
        // question ends with QTYPE=A, QCLASS=IN
        Assert.Equal(DnsWire.QtypeA, U16(query.Length - 4));
        Assert.Equal(1, U16(query.Length - 2));
    }

    [Fact]
    public void BuildQueryRejectsEmptyLabel()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery("bad..name"));
    }

    [Fact]
    public void ParsesARecordsAndMinTtl()
    {
        var response = BuildResponse(
            "example.com",
            (300, "93.184.216.34"),
            (120, "93.184.216.35"));

        var answer = DnsWire.ParseResponse(response);

        Assert.Equal([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35")], answer.Ips);
        Assert.Equal(120u, answer.MinTtl);
    }

    [Fact]
    public void SkipsCnameAnswers()
    {
        // CNAME first, then A: the parser must skip CNAME and collect A
        var writer = new MemoryStream();
        WriteHeader(writer, qd: 1, an: 2);
        WriteQuestion(writer, "www.example.com");
        WriteName(writer, "www.example.com");
        WriteResourceData(writer, type: 5, ttl: 60, rdata: BuildNameBytes("example.com"));
        WriteName(writer, "example.com");
        WriteResourceData(writer, type: 1, ttl: 240, rdata: [10, 0, 0, 1]);

        var answer = DnsWire.ParseResponse(writer.ToArray());

        Assert.Equal([IPAddress.Parse("10.0.0.1")], answer.Ips);
        Assert.Equal(240u, answer.MinTtl);
    }

    [Fact]
    public void HandlesNameCompression()
    {
        // the answer name is a pointer to the question (0xC00C)
        var writer = new MemoryStream();
        WriteHeader(writer, qd: 1, an: 1);
        WriteQuestion(writer, "example.com");
        writer.WriteByte(0xC0);
        writer.WriteByte(0x0C);
        WriteResourceData(writer, type: 1, ttl: 60, rdata: [203, 0, 113, 4]);

        var answer = DnsWire.ParseResponse(writer.ToArray());

        Assert.Equal([IPAddress.Parse("203.0.113.4")], answer.Ips);
    }

    [Fact]
    public void NxdomainIsANegativeAnswer()
    {
        var writer = new MemoryStream();
        writer.Write([0xAB, 0xCD, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0]); // rcode=3 NXDOMAIN
        WriteQuestion(writer, "gone.example");

        Assert.Throws<DnsNegativeAnswerException>(() => DnsWire.ParseResponse(writer.ToArray()));
    }

    [Fact]
    public void ThrowsOnTruncatedMessage()
    {
        Assert.Throws<FormatException>(() => DnsWire.ParseResponse([0xAB, 0xCD, 0x81, 0x80]));
    }

    private static byte[] BuildResponse(string question, params (int Ttl, string Ip)[] answers)
    {
        var writer = new MemoryStream();
        WriteHeader(writer, qd: 1, an: answers.Length);
        WriteQuestion(writer, question);
        foreach (var (ttl, ip) in answers)
        {
            WriteName(writer, question);
            WriteResourceData(writer, type: 1, ttl: ttl, rdata: IPAddress.Parse(ip).GetAddressBytes());
        }

        return writer.ToArray();
    }

    private static void WriteHeader(MemoryStream writer, int qd, int an)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[..2], 0xABCD);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x8180); // QR=1, RD, RA, rcode=0
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], (ushort)qd);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)an);
        writer.Write(header);
    }

    private static void WriteQuestion(MemoryStream writer, string name)
    {
        WriteName(writer, name);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[..2], 1);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        writer.Write(tail);
    }

    private static void WriteName(MemoryStream writer, string name)
    {
        foreach (var label in name.Split('.'))
        {
            writer.WriteByte((byte)label.Length);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }

        writer.WriteByte(0);
    }

    private static byte[] BuildNameBytes(string name)
    {
        var buffer = new MemoryStream();
        WriteName(buffer, name);
        return buffer.ToArray();
    }

    private static void WriteResourceData(MemoryStream writer, int type, int ttl, byte[] rdata)
    {
        Span<byte> fixedPart = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[..2], (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..4], 1);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..8], (uint)ttl);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..], (ushort)rdata.Length);
        writer.Write(fixedPart);
        writer.Write(rdata);
    }
}
