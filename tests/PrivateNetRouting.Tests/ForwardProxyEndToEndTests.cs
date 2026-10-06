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
                Socks5Upstream = "127.0.0.1:1",
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
        Func<string, Core.Net.Dns.ResolveResult> resolve)
    {
        var log = new FileLog(Path.Combine(_tempDir, "proxy.log"));
        var config = new ForwardProxyConfig
        {
            Listen = "127.0.0.1:0",
            BindWslGateway = false,
            Socks5Upstream = socksUpstream,
            HeaderTimeoutSeconds = 5,
            SocksTimeoutSeconds = 5,
        };
        var proxy = new ForwardProxyService(
            config,
            new FakeResolver(resolve),
            new Socks5Client(socksUpstream),
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

    /// <summary>SOCKS5 upstream that really connects to the requested IP:port and relays bytes.</summary>
    private async Task<string> StartRelaySocksAsync()
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

                    await stream.WriteAsync(new byte[] { 5, 0 });
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

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);
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
