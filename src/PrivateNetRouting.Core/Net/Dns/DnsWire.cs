using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Minimal DNS wire format codec (RFC 1035): A-record query and response parsing.
/// Only what the DoH resolver needs: A records and the minimum response TTL.
/// </summary>
public static class DnsWire
{
    public const ushort QtypeA = 1;
    private const int RcodeNameError = 3;

    /// <summary>Builds a single-record query (RD=1, QDCOUNT=1).</summary>
    public static byte[] BuildQuery(string name, ushort qtype = QtypeA, ushort id = 0xABCD)
    {
        var writer = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1);       // QDCOUNT
        writer.Write(header);

        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is < 1 or > 63)
            {
                throw new ArgumentException($"invalid label '{label}' in name {name}");
            }

            writer.WriteByte((byte)bytes.Length);
            writer.Write(bytes);
        }

        writer.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[..2], qtype);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..4], 1); // IN
        writer.Write(tail);
        return writer.ToArray();
    }

    /// <summary>Parses the response: A addresses and the minimum TTL among them.</summary>
    public static DnsAnswer ParseResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new FormatException("dns: message shorter than the header");
        }

        var rcode = data[3] & 0x0F;
        if (rcode == RcodeNameError)
        {
            throw new DnsNegativeAnswerException("dns: no such name (NXDOMAIN)");
        }

        if (rcode != 0)
        {
            throw new FormatException($"dns: server returned rcode {rcode}");
        }

        var qdcount = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        var ancount = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);

        var offset = 12;
        for (var i = 0; i < qdcount; i++)
        {
            offset = SkipName(data, offset);
            offset += 4; // QTYPE + QCLASS
        }

        List<IPAddress>? ips = null;
        uint? minTtl = null;
        for (var i = 0; i < ancount; i++)
        {
            offset = SkipName(data, offset);
            if (offset + 10 > data.Length)
            {
                throw new FormatException("dns: truncated answer record");
            }

            var rtype = BinaryPrimitives.ReadUInt16BigEndian(data[offset..(offset + 2)]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..(offset + 8)]);
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..(offset + 10)]);
            offset += 10;
            if (offset + rdlen > data.Length)
            {
                throw new FormatException("dns: truncated RDATA");
            }

            if (rtype == QtypeA && rdlen == 4)
            {
                ips ??= [];
                ips.Add(new IPAddress(data[offset..(offset + 4)]));
                minTtl = minTtl is null ? ttl : Math.Min(minTtl.Value, ttl);
            }

            offset += rdlen;
        }

        return new DnsAnswer(ips ?? [], minTtl);
    }

    /// <summary>Skips a name (including 0xC0 pointer compression).</summary>
    private static int SkipName(ReadOnlySpan<byte> data, int offset)
    {
        while (true)
        {
            if (offset >= data.Length)
            {
                throw new FormatException("dns: name out of message bounds");
            }

            var length = data[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2;
            }

            offset += 1 + length;
        }
    }
}

/// <summary>
/// A valid negative answer — the name does not exist (NXDOMAIN) or has no A record (NODATA). The
/// endpoint worked: it must not be marked dead, and the answer is cached for a short time.
/// </summary>
public sealed class DnsNegativeAnswerException(string message) : FormatException(message);

/// <summary>Parsed response: A addresses and the minimum TTL (sec), if addresses are present.</summary>
public sealed record DnsAnswer(IReadOnlyList<IPAddress> Ips, uint? MinTtl);
