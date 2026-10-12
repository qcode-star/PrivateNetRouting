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

    [Fact]
    public async Task ConnectsTunnelsAndCounts()
    {
        var origin = await StartEchoServerAsync();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var socksUpstream = await StartRelaySocksAsync();
        var (proxy, proxyEndpoint) = await StartProxyAsync(
            socksUpstream,
            host => new ResolveResult(IPAddress.Loopback, "fake", 300));

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
            host => new ResolveResult(IPAddress.Loopback, "fake", 300),
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
            host => new ResolveResult(IPAddress.Loopback, "fake", 300));

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
            host => new ResolveResult(IPAddress.Loopback, "fake", 300));

        var alive = await ProxyHealthCheck.CheckAsync(
            "127.0.0.1", proxyEndpoint.Port, "health.example", originPort, TimeSpan.FromSeconds(5));

        Assert.True(alive);
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
            host => new ResolveResult(IPAddress.Loopback, "fake", 300));

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
                new FakeResolver(host => new ResolveResult(IPAddress.Loopback, "fake", 300)),
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

    private async Task<(ForwardProxyService Proxy, IPEndPoint Endpoint)> StartProxyAsync(
        string socksUpstream,
        Func<string, Core.Net.Dns.ResolveResult> resolve,
        string? user = null,
        string? password = null)
    {
        var log = new FileLog(Path.Combine(_tempDir, "proxy.log"));
        var config = new ForwardProxyConfig
        {
            Listen = "127.0.0.1:0",
            BindWslGateway = false,
            HeaderTimeoutSeconds = 5,
            SocksTimeoutSeconds = 5,
        };
        var proxy = new ForwardProxyService(
            config,
            new FakeResolver(resolve),
            new Socks5Client(socksUpstream, user, password),
            new ActivityMarker(Path.Combine(_tempDir, "marker"), TimeSpan.FromSeconds(60)),
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

    /// <summary>
    /// SOCKS5 upstream that really connects to the requested IP:port and relays bytes;
    /// with credentials — the RFC 1929 subnegotiation first, and only the exact pair opens the tunnel.
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

                    var ip = new IPAddress(request[4..8]);
                    var port = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8, 2));
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });

                    try
                    {
                        using var target = new TcpClient();
                        await target.ConnectAsync(ip, port);
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
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (IOException)
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
