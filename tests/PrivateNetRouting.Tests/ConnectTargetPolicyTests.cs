using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Runtime;

namespace PrivateNetRouting.Tests;

public class ConnectTargetPolicyTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-policy-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("api.localhost")]
    public void RefusesTargetsThatWouldReachTheUpstreamsOwnHost(string host)
    {
        Assert.True(ConnectTargetPolicy.IsForbidden(host));
    }

    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("10.1.2.3")]       // private ranges and the tailnet are what the chain is for
    [InlineData("100.64.1.2")]
    [InlineData("192.168.1.1")]
    [InlineData("2001:db8::1")]
    [InlineData("api.example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("notlocalhost")]
    public void AllowsEverythingElse(string host)
    {
        Assert.False(ConnectTargetPolicy.IsForbidden(host));
    }

    [Fact]
    public async Task TheProxyAnswers403ToALoopbackTargetWithoutDialing()
    {
        var dials = 0;
        var socks = new TcpListener(IPAddress.Loopback, 0);
        socks.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var session = await socks.AcceptTcpClientAsync();
                    Interlocked.Increment(ref dials);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        });
        var config = new ForwardProxyConfig { Listen = "127.0.0.1:0", BindWslGateway = false, HeaderTimeoutSeconds = 5, SocksTimeoutSeconds = 5 };
        await using var proxy = new ForwardProxyService(
            config,
            new FakeResolver(),
            new Socks5Client(((IPEndPoint)socks.LocalEndpoint).ToString()),
            new ActivityMarker(Path.Combine(_tempDir, "marker"), TimeSpan.FromSeconds(60)),
            new FileLog(Path.Combine(_tempDir, "proxy.log")));
        await proxy.StartAsync(CancellationToken.None);
        var endpoint = IPEndPoint.Parse(proxy.ListenerEndpoints[0]);

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:22 HTTP/1.1\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, timeout.Token);

        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal(0, dials); // nothing was sent towards the upstream
        Assert.Equal(0, proxy.FailedConnections); // a refusal is policy, not a failure
        socks.Stop();
    }
}
