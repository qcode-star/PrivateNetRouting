using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PrivateNetRouting.Core.Net.Dns;

/// <summary>
/// Минимальный кодек DNS-wire формата (RFC 1035): запрос A-записи и разбор ответа.
/// Только то, что нужно DoH-резолверу: A-записи и минимальный TTL ответа.
/// </summary>
public static class DnsWire
{
    public const ushort QtypeA = 1;

    /// <summary>Строит запрос одной записи (RD=1, QDCOUNT=1).</summary>
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
                throw new ArgumentException($"некорректная метка '{label}' в имени {name}");
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

    /// <summary>Разбор ответа: A-адреса и минимальный TTL среди них.</summary>
    public static DnsAnswer ParseResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new FormatException("dns: сообщение короче заголовка");
        }

        var rcode = data[3] & 0x0F;
        if (rcode != 0)
        {
            throw new FormatException($"dns: сервер вернул rcode {rcode}");
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
                throw new FormatException("dns: обрезанная запись ответа");
            }

            var rtype = BinaryPrimitives.ReadUInt16BigEndian(data[offset..(offset + 2)]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..(offset + 8)]);
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..(offset + 10)]);
            offset += 10;
            if (offset + rdlen > data.Length)
            {
                throw new FormatException("dns: обрезанный RDATA");
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

    /// <summary>Пропускает имя (включая сжатие указателем 0xC0).</summary>
    private static int SkipName(ReadOnlySpan<byte> data, int offset)
    {
        while (true)
        {
            if (offset >= data.Length)
            {
                throw new FormatException("dns: имя выходит за границы сообщения");
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

/// <summary>Разобранный ответ: A-адреса и минимальный TTL (сек), если адреса есть.</summary>
public sealed record DnsAnswer(IReadOnlyList<IPAddress> Ips, uint? MinTtl);
