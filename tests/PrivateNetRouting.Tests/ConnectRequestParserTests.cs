using System.Text;
using PrivateNetRouting.Core.Net.Proxy;

namespace PrivateNetRouting.Tests;

public class ConnectRequestParserTests
{
    [Fact]
    public void ParsesHostAndPort()
    {
        var ok = ConnectRequestParser.TryParse("CONNECT api.example.com:443 HTTP/1.1\r\n\r\n"u8, out var request, out _);
        Assert.True(ok);
        Assert.Equal("api.example.com", request!.Host);
        Assert.Equal(443, request.Port);
    }

    [Fact]
    public void DefaultsToPort443()
    {
        var ok = ConnectRequestParser.TryParse("CONNECT example.com HTTP/1.1\r\n"u8, out var request, out _);
        Assert.True(ok);
        Assert.Equal(443, request!.Port);
    }

    [Fact]
    public void NormalizesHost()
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes("CONNECT Example.COM.:8443 HTTP/1.0\r\n\r\n"), out var request, out _);
        Assert.True(ok);
        Assert.Equal("example.com", request!.Host);
        Assert.Equal(8443, request.Port);
    }

    [Fact]
    public void RejectsOverlongLabelAndName()
    {
        var label = new string('a', 64);
        Assert.False(ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes($"CONNECT {label}.example.com:443 HTTP/1.1\r\n\r\n"), out _, out _));
        var name = string.Join('.', Enumerable.Repeat(new string('a', 60), 5));
        Assert.False(ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes($"CONNECT {name}:443 HTTP/1.1\r\n\r\n"), out _, out _));
    }

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1")]
    [InlineData("CONNECT example.com:443")]              // no protocol version
    [InlineData("CONNECT example.com:0 HTTP/1.1")]       // port out of range
    [InlineData("CONNECT example.com:notaport HTTP/1.1")]
    [InlineData("CONNECT :443 HTTP/1.1")]                // empty host
    [InlineData("CONNECT 2001:db8::1:443 HTTP/1.1")]     // IPv6 literal without brackets: the port is ambiguous
    [InlineData("CONNECT [2001:db8::1 HTTP/1.1")]        // unclosed bracket
    [InlineData("CONNECT [2001:db8::1]:x HTTP/1.1")]     // bad port after the bracket
    [InlineData("CONNECT [2001:db8::1]443 HTTP/1.1")]    // no colon before the port
    [InlineData("CONNECT [fe80::1%5]:443 HTTP/1.1")]     // a zone id cannot ride a SOCKS request
    [InlineData("CONNECT [192.0.2.1]:443 HTTP/1.1")]     // brackets are for IPv6 only
    [InlineData("CONNECT [example.com]:443 HTTP/1.1")]
    [InlineData("CONNECT a:1:443 HTTP/1.1")]             // a colon in a name
    [InlineData("CONNECT example.com:+443 HTTP/1.1")]    // a sign is not part of a port
    [InlineData("CONNECT example.com:-1 HTTP/1.1")]
    [InlineData("CONNECT 1:443 HTTP/1.1")]               // inet_aton shorthand: 0.0.0.1
    [InlineData("CONNECT 127.1:443 HTTP/1.1")]           // inet_aton shorthand: 127.0.0.1
    [InlineData("CONNECT 0x7f.1:443 HTTP/1.1")]
    [InlineData("CONNECT 0x7f000001:443 HTTP/1.1")]
    [InlineData("CONNECT 127.000.000.001:443 HTTP/1.1")] // non-canonical octets
    [InlineData("CONNECT 256.1.1.1:443 HTTP/1.1")]       // not an address and not a name
    [InlineData("CONNECT 203.0.113.4.5:443 HTTP/1.1")]
    [InlineData("CONNECT host.123:443 HTTP/1.1")]        // a numeric last label
    [InlineData("CONNECT a..b:443 HTTP/1.1")]            // empty label
    [InlineData("CONNECT .:443 HTTP/1.1")]               // nothing left after the trailing dot
    public void Rejects(string firstLine)
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes(firstLine + "\r\n\r\n"), out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("CONNECT [2001:db8::1]:8443 HTTP/1.1", "2001:db8::1", 8443, "[2001:db8::1]:8443")]
    [InlineData("CONNECT [2001:DB8::1] HTTP/1.1", "2001:db8::1", 443, "[2001:db8::1]:443")]
    public void ParsesBracketedIpv6Literals(string firstLine, string host, int port, string text)
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes(firstLine + "\r\n\r\n"), out var request, out _);
        Assert.True(ok);
        Assert.Equal(host, request!.Host);
        Assert.Equal(port, request.Port);
        Assert.Equal(text, request.ToString());
    }

    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("host1.example.com")]
    [InlineData("localhost")]
    public void AcceptsCanonicalIpv4AndNames(string host)
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\n\r\n"), out var request, out _);
        Assert.True(ok);
        Assert.Equal(host, request!.Host);
    }

    /// <summary>The error ends up in the log: a plain-HTTP request carries a URL, and a URL can carry tokens.</summary>
    [Theory]
    [InlineData("GET http://example.com/path?token=SECRET HTTP/1.1")]
    [InlineData("CONNECT http://example.com/path?token=SECRET:443 HTTP/1.1")]
    [InlineData("CONNECT example.com:443?token=SECRET HTTP/1.1")]
    public void ErrorsDoNotEchoUrlQueries(string firstLine)
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes(firstLine + "\r\n\r\n"), out _, out var error);

        Assert.False(ok);
        Assert.DoesNotContain("SECRET", error);
        Assert.DoesNotContain("token", error);
    }

    [Fact]
    public void RejectsWithoutCarriageReturn()
    {
        var ok = ConnectRequestParser.TryParse("CONNECT example.com:443 HTTP/1.1\n\n"u8, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AcceptsLowercaseConnect()
    {
        var ok = ConnectRequestParser.TryParse("connect example.com:443 HTTP/1.1\r\n\r\n"u8, out var request, out _);
        Assert.True(ok);
        Assert.Equal("example.com", request!.Host);
    }

    [Theory]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nX-Pnr-Probe: 1\r\n\r\n", true)]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nHost: x\r\nx-pnr-probe:1\r\n\r\n", true)]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\n\r\n", false)]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nUser-Agent: X-Pnr-Probe: 1\r\n\r\n", false)]
    public void DetectsTheProbeMarkerHeader(string header, bool isProbe)
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes(header), out var request, out _);
        Assert.True(ok);
        Assert.Equal(isProbe, request!.IsProbe);
    }
}
