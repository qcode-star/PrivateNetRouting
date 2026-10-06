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

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1")]
    [InlineData("CONNECT example.com:443")]              // no protocol version
    [InlineData("CONNECT example.com:0 HTTP/1.1")]       // port out of range
    [InlineData("CONNECT example.com:notaport HTTP/1.1")]
    [InlineData("CONNECT :443 HTTP/1.1")]                // empty host
    public void Rejects(string firstLine)
    {
        var ok = ConnectRequestParser.TryParse(Encoding.Latin1.GetBytes(firstLine + "\r\n\r\n"), out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
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
}
