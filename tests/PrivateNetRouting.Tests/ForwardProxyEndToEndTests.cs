using System.Buffers.Binary;
using System.Collections.Concurrent;
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

/// <summary>
/// End-to-end run of the forward proxy: client → proxy → fake SOCKS5 → echo server.
/// Verifies 200 on CONNECT, a two-way tunnel, 400 on non-CONNECT and the health check.
/// </summary>
public class ForwardProxyEndToEndTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-e2e-").FullName;
    private readonly List<TcpListener> _listeners = [];

    /// <summary>A documentation-range address (RFC 5737) the fixtures dial through the fake upstream: real-looking, not forbidden by the target policy, not routable.</summary>
    private static readonly IPAddress FixtureIp = IPAddress.Parse("192.0.2.10");

    [Fact]
    public async Task ConnectsTunnelsAndCounts()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));

        var status = await ReadLineAsync(stream);
        Assert.StartsWith("HTTP/1.1 200", status);

        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[16];
        var total = 0;
        while (total < 4)
        {
            var read = await stream.ReadAsync(echo.AsMemory(total));
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal("pong"u8.ToArray(), echo[..4]);
        Assert.True(proxy.TotalConnections >= 1);
    }

    [Fact]
    public async Task ConnectsThroughAuthenticatedUpstream()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync("alice", "s3cret");
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300),
            "alice",
            "s3cret");

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));

        var status = await ReadLineAsync(stream);
        Assert.StartsWith("HTTP/1.1 200", status);
    }

    [Fact]
    public async Task Replies400ToNonConnect()
    {
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray());

        var status = await ReadLineAsync(stream);
        Assert.StartsWith("HTTP/1.1 400", status);
    }

    [Fact]
    public async Task Replies502WhenResolveFails()
    {
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => throw new IOException("doh: no endpoint responded"));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync("CONNECT unresolvable.example:443 HTTP/1.1\r\n\r\n"u8.ToArray());

        var status = await ReadLineAsync(stream);
        Assert.StartsWith("HTTP/1.1 502", status);
        Assert.True(proxy.FailedConnections >= 1);
    }

    [Fact]
    public async Task HealthCheckPassesThroughRunningProxy()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        var alive = await ProxyHealthCheck.CheckAsync(
            "127.0.0.1", proxyEndpoint.Port, "health.example", originPort, TimeSpan.FromSeconds(5));

        Assert.True(alive);
    }

    [Fact]
    public async Task HealthProbeDoesNotTouchActivityMarker()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (_, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));
        var markerPath = Path.Combine(_tempDir, "marker");

        var alive = await ProxyHealthCheck.CheckAsync(
            "127.0.0.1", proxyEndpoint.Port, "health.example", originPort, TimeSpan.FromSeconds(5));
        Assert.True(alive);

        // the marker is touched right after the 200 is written: give a wrongly touching proxy time to do it
        await Task.Delay(300);
        Assert.False(File.Exists(markerPath), "a health probe counted as client activity");

        // a real client CONNECT does touch it
        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 200", await ReadLineAsync(stream));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(markerPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(markerPath), "client traffic did not touch the activity marker");
    }

    [Fact]
    public async Task Socks5ModePassesTargetNameToUpstream()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var (socksUpstream, seenName) = await StartNameRelaySocksAsync(originPort);

        // the resolver must never be called: in the socks5 mode the name goes to the upstream
        var log = new FileLog(Path.Combine(_tempDir, "proxy-passthrough.log"));
        var config = new ForwardProxyConfig
        {
            Listen = "127.0.0.1:0",
            BindWslGateway = false,
            HeaderTimeoutSeconds = 5,
            SocksTimeoutSeconds = 5,
        };
        var proxy = new ForwardProxyService(
            config,
            new FakeResolver(_ => throw new IOException("resolution must not happen in the socks5 mode")),
            new Socks5Client(socksUpstream),
            new ActivityMarker(Path.Combine(_tempDir, "marker-passthrough"), TimeSpan.FromSeconds(60)),
            log,
            passHostNameToUpstream: true);
        await proxy.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(ParseIpEndPoint(proxy.ListenerEndpoints[0]));
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));

        var status = await ReadLineAsync(stream);
        Assert.StartsWith("HTTP/1.1 200", status);

        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[16];
        var total = 0;
        while (total < 4)
        {
            var read = await stream.ReadAsync(echo.AsMemory(total));
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal("pong"u8.ToArray(), echo[..4]);
        Assert.Equal("target.example", await seenName);
    }

    /// <summary>
    /// StopAsync used to close only the listeners: tunnels already established kept relaying
    /// (ActiveConnections stayed above zero and the chain "stopped" while Claude still worked).
    /// </summary>
    [Fact]
    public async Task StopEndsEstablishedTunnels()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 200", await ReadLineAsync(stream, timeout.Token));
        Assert.Equal(1, proxy.ActiveConnections);

        await proxy.StopAsync();

        // the client sees the tunnel closed: EOF (or a reset) instead of an open, silent socket
        var buffer = new byte[16];
        var read = 0;
        try
        {
            read = await stream.ReadAsync(buffer, timeout.Token);
        }
        catch (IOException)
        {
        }

        Assert.Equal(0, read);
        while (proxy.ActiveConnections > 0)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>
    /// Regression of the "deaf restart": StopAsync used to cancel the token shared across
    /// starts, so a second StartAsync in the same process bound a listener whose accept loop
    /// died the moment it started — the port answered the TCP handshake and nobody read the
    /// requests (the health check failed silently). Each start must issue a fresh token and
    /// the proxy must serve again.
    /// </summary>
    [Fact]
    public async Task ServesAgainAfterStopAndStart()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, _) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        await proxy.StopAsync();
        await proxy.StartAsync(CancellationToken.None);
        var endpoint = ParseIpEndPoint(proxy.ListenerEndpoints[0]);

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var status = await ReadLineAsync(stream, timeout.Token);
        Assert.StartsWith("HTTP/1.1 200", status);
    }

    /// <summary>
    /// A client may send the first tunnel bytes (a TLS ClientHello) right behind the CONNECT
    /// header, in the same segment; they were read with the header and must reach the origin.
    /// </summary>
    [Fact]
    public async Task ForwardsBytesPipelinedBehindTheConnectHeader()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (_, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\nping"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<byte>();
        var buffer = new byte[64];
        while (!Encoding.ASCII.GetString(received.ToArray()).EndsWith("pong", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            Assert.True(read > 0, "the tunnel closed without answering the pipelined bytes");
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(received.ToArray()));
    }

    /// <summary>
    /// The client finishing its side (FIN) must not cut the answer still on its way back: the
    /// origin replies only after it has seen the end of the request.
    /// </summary>
    [Fact]
    public async Task HalfCloseFromTheClientStillDeliversTheAnswer()
    {
        var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        _listeners.Add(origin);
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var session = await origin.AcceptTcpClientAsync();
            var originStream = session.GetStream();
            var drain = new byte[64];
            while (await originStream.ReadAsync(drain) > 0)
            {
                // the request ends with the client's FIN
            }

            await originStream.WriteAsync("bye"u8.ToArray());
            session.Client.Shutdown(SocketShutdown.Send);
        });
        var socksUpstream = await StartRelaySocksAsync();
        var (_, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 200", await ReadLineAsync(stream, timeout.Token));

        client.Client.Shutdown(SocketShutdown.Send);

        var answer = new MemoryStream();
        await stream.CopyToAsync(answer, timeout.Token); // until the proxy ends the tunnel
        Assert.Equal("bye", Encoding.ASCII.GetString(answer.ToArray()));
    }

    /// <summary>
    /// Start and stop calls racing each other must leave a consistent service: whatever the
    /// order, the last stop leaves nothing bound and a following start serves again.
    /// </summary>
    [Fact]
    public async Task ConcurrentStartAndStopLeaveNothingBound()
    {
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, _) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300));
        await proxy.StopAsync();

        for (var round = 0; round < 20; round++)
        {
            await Task.WhenAll(
                Task.Run(() => proxy.StartAsync(CancellationToken.None)),
                Task.Run(() => proxy.StopAsync()),
                Task.Run(() => proxy.StartAsync(CancellationToken.None)),
                Task.Run(() => proxy.StopAsync()));
            await proxy.StopAsync();

            Assert.False(proxy.IsRunning);
            Assert.Empty(proxy.ListenerEndpoints);
        }

        await proxy.StartAsync(CancellationToken.None);
        Assert.True(proxy.IsRunning);
        Assert.Single(proxy.ListenerEndpoints);
    }

    /// <summary>
    /// Regression of the GUI deadlock on exit: OnExit synchronously waits for DisposeAsync under a
    /// SynchronizationContext whose thread is blocked by that same wait (Dispatcher).
    /// Stop await continuations must go to the pool, not to the captured context.
    /// </summary>
    [Fact]
#pragma warning disable xUnit1031 // the blocking wait here is exactly the scenario under test
    public void StopCompletesUnderBlockedSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var trapped = new TrappedPostContext();
        SynchronizationContext.SetSynchronizationContext(trapped);
        try
        {
            var log = new FileLog(Path.Combine(_tempDir, "proxy-stop-context.log"));
            var config = new ForwardProxyConfig
            {
                Listen = "127.0.0.1:0",
                BindWslGateway = false,
                HeaderTimeoutSeconds = 5,
                SocksTimeoutSeconds = 5,
            };
            var proxy = new ForwardProxyService(
                config,
                new FakeResolver(host => new ResolveResult(FixtureIp, "fake", 300)),
                new Socks5Client("127.0.0.1:1"),
                new ActivityMarker(Path.Combine(_tempDir, "marker-context"), TimeSpan.FromSeconds(60)),
                log);
            proxy.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var stop = proxy.DisposeAsync().AsTask();
            Assert.True(
                stop.Wait(TimeSpan.FromSeconds(5)),
                "DisposeAsync did not complete: stop continuation was posted to the blocked SynchronizationContext");

            trapped.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
#pragma warning restore xUnit1031

    /// <summary>"Blocked dispatcher" context: Post enqueues and never executes.</summary>
    private sealed class TrappedPostContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posts = new();

        public override void Post(SendOrPostCallback d, object? state) =>
            _posts.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("unexpected Send on trapped context");

        public void Drain()
        {
            while (_posts.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }

    /// <summary>
    /// A name that resolves to several addresses: the upstream refuses the first one, the second
    /// is dialed — the answer used to be reduced to its first address.
    /// </summary>
    [Fact]
    public async Task DialsTheNextAddressWhenTheFirstIsRefused()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var refused = IPAddress.Parse("192.0.2.1");
        var socksUpstream = await StartRefusingSocksAsync(originPort, refused);
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(refused, "fake", 300, [IPAddress.Parse("192.0.2.2")]));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 200", await ReadLineAsync(stream, timeout.Token));

        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[16];
        var read = await stream.ReadAsync(echo, timeout.Token);
        Assert.Equal("pong"u8.ToArray(), echo[..read]);
        Assert.Equal(0, proxy.FailedConnections);
    }

    /// <summary>
    /// The 403 policy covers the resolved addresses too: a name that resolves into the upstream
    /// host's own loopback or link-local (DNS rebinding, a hostile or misconfigured resolver) is
    /// refused without a single dial.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task RefusesANameThatResolvesToAForbiddenAddress(string resolved)
    {
        var socksUpstream = await StartCountingSocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(IPAddress.Parse(resolved), "fake", 300));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync("CONNECT target.example:22 HTTP/1.1\r\n\r\n"u8.ToArray());

        var status = await ReadLineAsync(stream);
        Assert.StartsWith("HTTP/1.1 403", status);
        Assert.Equal(0, _upstreamDials); // nothing was sent towards the upstream
        Assert.Equal(0, proxy.FailedConnections); // a refusal is policy, not a failure
    }

    /// <summary>
    /// A mixed resolution: the forbidden address is dropped, the allowed one is dialed — the name
    /// still works. The forbidden one comes first, so a proxy that ignored the policy would dial it.
    /// </summary>
    [Fact]
    public async Task DialsOnlyTheAllowedAddressesOfAMixedResolution()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(IPAddress.Loopback, "fake", 300, [FixtureIp]));

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 200", await ReadLineAsync(stream, timeout.Token));

        await stream.WriteAsync("ping"u8.ToArray());
        var echo = new byte[16];
        var read = await stream.ReadAsync(echo, timeout.Token);
        Assert.Equal("pong"u8.ToArray(), echo[..read]);
        Assert.Equal(0, proxy.FailedConnections);
        Assert.Equal([(1, FixtureIp)], _relayRequests.ToArray()); // one dial, by IPv4, of the allowed address only
    }

    private int _upstreamDials;

    /// <summary>SOCKS5 upstream that only counts handshakes: the refusal tests assert nothing ever reaches it.</summary>
    private async Task<string> StartCountingSocksAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient session;
                try
                {
                    session = await listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _upstreamDials);
                session.Dispose();
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).ToString();
    }

    /// <summary>
    /// A failure after the 200 (here: the activity marker throws right behind it) must not write a
    /// 502 into the tunnel — the client already speaks TLS there — nor count as a failed CONNECT.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheEstablishedAnswerWritesNo502()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300),
            markerTime: new ThrowingTimestampProvider());

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT target.example:{originPort} HTTP/1.1\r\n\r\n"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<byte>();
        var buffer = new byte[256];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var text = Encoding.ASCII.GetString(received.ToArray());
        Assert.StartsWith("HTTP/1.1 200", text);
        Assert.DoesNotContain("502", text);
        Assert.Equal(0, proxy.FailedConnections);
    }

    /// <summary>
    /// A socket error that keeps repeating on accept: the loop backs off, warns once per error
    /// code, and a listener taken down during the pause ("not listening") ends it without a fault.
    /// </summary>
    [Fact]
    public async Task TheAcceptLoopBacksOffWarnsOnceAndEndsOnAStoppedListener()
    {
        var logPath = Path.Combine(_tempDir, "accept.log");
        using var log = new FileLog(logPath);
        var failures = 0;
        var delays = new List<TimeSpan>();

        var loop = ForwardProxyService.RunAcceptLoopAsync(
            _ => ++failures switch
            {
                <= 6 => throw new SocketException((int)SocketError.ConnectionReset),
                7 => throw new SocketException((int)SocketError.NoBufferSpaceAvailable),
                _ => throw new InvalidOperationException("Not listening. You must call the Start() method before calling this method."),
            },
            _ => { },
            log,
            CancellationToken.None,
            (pause, _) =>
            {
                delays.Add(pause);
                return Task.CompletedTask;
            });
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(loop.IsCompletedSuccessfully);
        Assert.Equal(
            [50, 100, 200, 400, 800, 1600, 2000],
            delays.Select(d => (int)d.TotalMilliseconds));
        var text = File.ReadAllText(logPath);
        Assert.Equal(1, text.Split("accept failed (ConnectionReset)").Length - 1);
        Assert.Equal(1, text.Split("accept failed (NoBufferSpaceAvailable)").Length - 1);
    }

    [Fact]
    public async Task ASuccessfulAcceptResetsTheBackoffAndTheWarning()
    {
        var logPath = Path.Combine(_tempDir, "accept-reset.log");
        using var log = new FileLog(logPath);
        var calls = 0;
        var delays = new List<TimeSpan>();

        await ForwardProxyService.RunAcceptLoopAsync(
            _ => ++calls switch
            {
                1 or 2 or 4 => throw new SocketException((int)SocketError.ConnectionReset),
                3 => ValueTask.FromResult(new TcpClient()),
                _ => throw new ObjectDisposedException("listener"),
            },
            client => client.Dispose(),
            log,
            CancellationToken.None,
            (pause, _) =>
            {
                delays.Add(pause);
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([50, 100, 50], delays.Select(d => (int)d.TotalMilliseconds));
        Assert.Equal(2, File.ReadAllText(logPath).Split("accept failed (ConnectionReset)").Length - 1);
    }

    /// <summary>
    /// A client that connects and never sends its request (a port scan, an idle pre-connect) is
    /// closed without a 502 and is not a proxy failure.
    /// </summary>
    [Fact]
    public async Task AnIdleClientIsClosedQuietlyAndNotCountedAsAFailure()
    {
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(FixtureIp, "fake", 300),
            headerTimeoutSeconds: 1);

        using var client = new TcpClient();
        await client.ConnectAsync(proxyEndpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var read = await client.GetStream().ReadAsync(new byte[64], timeout.Token);

        Assert.Equal(0, read); // closed, nothing written (no 502)
        Assert.Equal(0, proxy.FailedConnections);
    }

    /// <summary>SOCKS5 upstream that answers "connection refused" for one IPv4 address and relays every other to the origin.</summary>
    private async Task<string> StartRefusingSocksAsync(int originPort, IPAddress refusedAddress)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient session;
                try
                {
                    session = await listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using var _ = session;
                    var stream = session.GetStream();
                    if (!await TryReadExactAsync(stream, new byte[3]))
                    {
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 5, 0 });
                    var request = new byte[10]; // VER CMD RSV ATYP=1 IPv4 port
                    if (!await TryReadExactAsync(stream, request))
                    {
                        return;
                    }

                    if (new IPAddress(request[4..8]).Equals(refusedAddress))
                    {
                        await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }); // REP=5: refused
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    try
                    {
                        using var target = new TcpClient();
                        await target.ConnectAsync(IPAddress.Loopback, originPort);
                        var targetStream = target.GetStream();
                        await Task.WhenAll(
                            RelayAsync(stream, targetStream),
                            RelayAsync(targetStream, stream));
                    }
                    catch (SocketException)
                    {
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).ToString();
    }

    private async Task<(ForwardProxyService Proxy, IPEndPoint Endpoint)> StartProxyAsync(
        string socksUpstream,
        Func<string, Core.Net.Dns.ResolveResult> resolve,
        string? user = null,
        string? password = null,
        int headerTimeoutSeconds = 5,
        TimeProvider? markerTime = null)
    {
        var log = new FileLog(Path.Combine(_tempDir, "proxy.log"));
        var config = new ForwardProxyConfig
        {
            Listen = "127.0.0.1:0",
            BindWslGateway = false,
            HeaderTimeoutSeconds = headerTimeoutSeconds,
            SocksTimeoutSeconds = 5,
        };
        var proxy = new ForwardProxyService(
            config,
            new FakeResolver(resolve),
            new Socks5Client(socksUpstream, user, password),
            new ActivityMarker(Path.Combine(_tempDir, "marker"), TimeSpan.FromSeconds(60), markerTime),
            log);
        await proxy.StartAsync(CancellationToken.None);
        return (proxy, ParseIpEndPoint(proxy.ListenerEndpoints[0]));
    }

    private static IPEndPoint ParseIpEndPoint(string hostPort)
    {
        var parts = hostPort.Split(':');
        return new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
    }

    /// <summary>Echo server: answers "pong" to "ping", echoes everything else.</summary>
    private async Task<TcpListener> StartEchoServerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient session;
                try
                {
                    session = await listener.AcceptTcpClientAsync();
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
                    var buffer = new byte[4096];
                    try
                    {
                        while (true)
                        {
                            var read = await stream.ReadAsync(buffer);
                            if (read == 0)
                            {
                                return;
                            }

                            var reply = buffer[..read].SequenceEqual("ping"u8)
                                ? "pong"u8.ToArray()
                                : buffer[..read];
                            await stream.WriteAsync(reply);
                        }
                    }
                    catch (IOException)
                    {
                    }
                });
            }
        });
        return listener;
    }

    /// <summary>What the relay SOCKS5 fixture was asked to dial: the ATYP and, for an IPv4 request, the address.</summary>
    private readonly ConcurrentQueue<(byte Atyp, IPAddress? Address)> _relayRequests = new();

    /// <summary>
    /// SOCKS5 upstream that accepts the CONNECT and relays bytes; the requested address is not
    /// dialed (the fixtures dial a documentation-range address nothing here can route), only
    /// recorded in <see cref="_relayRequests"/> — the relay connects to the requested port on the
    /// loopback, standing in for "the upstream reached the target". With credentials — the
    /// RFC 1929 subnegotiation first, and only the exact pair opens the tunnel.
    /// </summary>
    private async Task<string> StartRelaySocksAsync(string? user = null, string? password = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient session;
                try
                {
                    session = await listener.AcceptTcpClientAsync();
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

                    if (user is null)
                    {
                        await stream.WriteAsync(new byte[] { 5, 0 });
                    }
                    else
                    {
                        await stream.WriteAsync(new byte[] { 5, 2 });
                        var subnegotiation = new byte[2]; // VER ULEN
                        if (!await TryReadExactAsync(stream, subnegotiation) || subnegotiation[0] != 1)
                        {
                            return;
                        }

                        var uname = new byte[subnegotiation[1]];
                        var plen = new byte[1];
                        if (!await TryReadExactAsync(stream, uname) || !await TryReadExactAsync(stream, plen))
                        {
                            return;
                        }

                        var passwd = new byte[plen[0]];
                        if (!await TryReadExactAsync(stream, passwd))
                        {
                            return;
                        }

                        var ok = Encoding.UTF8.GetString(uname) == user
                            && Encoding.UTF8.GetString(passwd) == password;
                        await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 });
                        if (!ok)
                        {
                            return;
                        }
                    }

                    var request = new byte[10];
                    if (!await TryReadExactAsync(stream, request))
                    {
                        return;
                    }

                    // the requested address is only recorded (see the helper's doc comment), the port is what the tunnel aims at
                    _relayRequests.Enqueue((request[3], request[3] == 1 ? new IPAddress(request.AsSpan(4, 4)) : null));
                    var port = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8, 2));
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });

                    try
                    {
                        using var target = new TcpClient();
                        await target.ConnectAsync(IPAddress.Loopback, port);
                        var targetStream = target.GetStream();
                        await Task.WhenAll(
                            RelayAsync(stream, targetStream),
                            RelayAsync(targetStream, stream));
                    }
                    catch (SocketException)
                    {
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).ToString();
    }

    /// <summary>
    /// SOCKS5 upstream for the passthrough tests: accepts a CONNECT by name (ATYP=3), records
    /// the seen name and relays to the prearranged local port (stands in for "the upstream
    /// resolved the name").
    /// </summary>
    private async Task<(string Upstream, Task<string> SeenName)> StartNameRelaySocksAsync(int originPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var tcs = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient session;
                try
                {
                    session = await listener.AcceptTcpClientAsync();
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

                    var header = new byte[4]; // VER CMD RSV ATYP
                    if (!await TryReadExactAsync(stream, header) || header[3] != 0x03)
                    {
                        return;
                    }

                    var len = new byte[1];
                    if (!await TryReadExactAsync(stream, len))
                    {
                        return;
                    }

                    var name = new byte[len[0]];
                    if (!await TryReadExactAsync(stream, name))
                    {
                        return;
                    }

                    var portBytes = new byte[2];
                    if (!await TryReadExactAsync(stream, portBytes))
                    {
                        return;
                    }

                    tcs.TrySetResult(Encoding.UTF8.GetString(name));
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });

                    try
                    {
                        using var target = new TcpClient();
                        await target.ConnectAsync(IPAddress.Loopback, originPort);
                        var targetStream = target.GetStream();
                        await Task.WhenAll(
                            RelayAsync(stream, targetStream),
                            RelayAsync(targetStream, stream));
                    }
                    catch (SocketException)
                    {
                    }
                });
            }
        });
        return (((IPEndPoint)listener.LocalEndpoint).ToString(), tcs.Task);
    }

    private static async Task RelayAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer);
                if (read == 0)
                {
                    // a real SOCKS relay passes the end of one direction on as a half-close
                    to.Socket.Shutdown(SocketShutdown.Send);
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
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

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct = default)
    {
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, ct);
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        return text.Split("\r\n")[0];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100)); // let the listener closures settle
        Directory.Delete(_tempDir, recursive: true);
    }
}
