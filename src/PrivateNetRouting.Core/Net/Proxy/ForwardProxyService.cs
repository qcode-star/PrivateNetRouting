using System.Net;
using System.Net.Sockets;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.Net.Dns;
using PrivateNetRouting.Core.Net.Socks;
using PrivateNetRouting.Core.Runtime;

namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>Proxy statistics for the diagnostics summary (implementation — ForwardProxyService).</summary>
public interface IProxyStatistics
{
    bool IsRunning { get; }

    IReadOnlyList<string> ListenerEndpoints { get; }

    int ActiveConnections { get; }

    long TotalConnections { get; }

    long FailedConnections { get; }
}

/// <summary>
/// A single proxy instance from the supervisor's point of view: control and statistics.
/// IsRunning is redeclared to disambiguate the two parent interfaces.
/// </summary>
public interface IForwardProxyInstance : IProxyControl, IProxyStatistics
{
    new bool IsRunning { get; }
}

/// <summary>
/// Local CONNECT proxy. In the tailscale mode it resolves names via DoH through the SOCKS5
/// upstream — system resolution is not used (it is overridden by the bans) — and dials by IP
/// over the same SOCKS5. In the socks5 mode the trusted external upstream resolves on its side:
/// the target name rides the CONNECT itself (ATYP=3, no DoH machinery exists in that mode at
/// all). It binds to the primary address from the config and once on the WSL NAT gateway when
/// the adapter appears. Normal mode — silence in the log: start/stop, warnings, errors;
/// per-request tracing — Debug.
/// </summary>
public sealed class ForwardProxyService : IForwardProxyInstance, IAsyncDisposable
{
    private static readonly byte[] EstablishResponse =
        "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

    private static readonly byte[] BadRequestResponse =
        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private static readonly byte[] BadGatewayResponse =
        "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private readonly ForwardProxyConfig _config;
    private readonly IDnsResolver? _resolver;
    private readonly Socks5Client _socks;
    private readonly ActivityMarker _marker;
    private readonly FileLog _log;
    private readonly bool _passHostName;

    /// <summary>Per start: StopAsync cancels the token, and a fresh one is issued on the next
    /// StartAsync — a reused token would kill the accept loop the moment it starts.</summary>
    private CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly object _gate = new();
    private Task? _wslBindTask;
    private int _activeConnections;
    private long _totalConnections;
    private long _failedConnections;
    private volatile bool _running;

    public ForwardProxyService(
        ForwardProxyConfig config,
        IDnsResolver? resolver,
        Socks5Client socks,
        ActivityMarker marker,
        FileLog log,
        bool passHostNameToUpstream = false)
    {
        _config = config;
        _resolver = resolver;
        _socks = socks;
        _marker = marker;
        _log = log;
        _passHostName = passHostNameToUpstream;
    }

    public bool IsRunning => _running;

    public IReadOnlyList<string> ListenerEndpoints
    {
        get
        {
            lock (_gate)
            {
                return _listeners
                    .Where(l => l.LocalEndpoint is IPEndPoint)
                    .Select(l => ((IPEndPoint)l.LocalEndpoint!).ToString())
                    .ToList();
            }
        }
    }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public long TotalConnections => Volatile.Read(ref _totalConnections);

    public long FailedConnections => Volatile.Read(ref _failedConnections);

    public async Task StartAsync(CancellationToken ct)
    {
        if (_running)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var listen = Socks5Client.ParseEndPoint(_config.Listen);
        var listener = BindListener(listen);
        _ = AcceptLoopAsync(listener);
        _log.Info($"forward-proxy: listening on {listen}");

        if (_config.BindWslGateway)
        {
            _wslBindTask = Task.Run(() => WslBindWaitAsync());
        }

        _running = true;
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        // ConfigureAwait is mandatory: the shutdown is awaited blocking (OnExit GUI) under the
        // Dispatcher context — a captured context would deadlock the wait
        await _cts.CancelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }

            _listeners.Clear();
        }

        _log.Info("forward-proxy: stopped");
    }

    /// <summary>One-time bind on the WSL gateway: wait for the adapter with retries, bind, exit.</summary>
    private async Task WslBindWaitAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var gateway = WslGateway.FindIpv4();
            if (gateway is not null)
            {
                try
                {
                    var listener = BindListener(new IPEndPoint(gateway, ((IPEndPoint)Socks5Client.ParseEndPoint(_config.Listen)).Port));
                    _ = AcceptLoopAsync(listener);
                    _log.Info($"forward-proxy: listening on WSL gateway {gateway} (one-time bind)");
                }
                catch (SocketException ex)
                {
                    _log.Error($"forward-proxy: bind on WSL gateway {gateway} failed: {ex.Message}");
                }

                return;
            }

            _log.Debug("forward-proxy: WSL gateway adapter not found, retrying in 30 s");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TcpListener BindListener(IPEndPoint endpoint)
    {
        var listener = new TcpListener(endpoint);
        listener.Start(backlog: 128);
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return listener;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                // without capturing the caller's context: the accept loop must not depend
                // on the liveness of the UI thread (continuations — to the pool)
                client = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // the listener was taken down by the service stop
                return;
            }
            catch (SocketException ex) when (_cts.IsCancellationRequested
                || ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.Shutdown)
            {
                // the listener was stopped under the pending accept
                return;
            }
            catch (SocketException ex)
            {
                // a client reset in the backlog or a transient resource shortage must not end the
                // loop: the listener stays bound and would answer nobody
                _log.Warn($"forward-proxy: accept failed ({ex.SocketErrorCode}), continuing: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        // the token of the start that accepted this client: a stop must end its tunnels, and a
        // later start must not hand them its fresh one
        var stopToken = _cts.Token;
        using var _ = client;
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
        try
        {
            var stream = client.GetStream();

            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            headerCts.CancelAfter(TimeSpan.FromSeconds(_config.HeaderTimeoutSeconds));
            var header = await ReadHeaderAsync(stream, headerCts.Token);
            if (header is null)
            {
                return;
            }

            if (!ConnectRequestParser.TryParse(header, out var request, out var parseError))
            {
                await stream.WriteAsync(BadRequestResponse, _cts.Token);
                _log.Warn($"forward-proxy: {client.Client.RemoteEndPoint}: {parseError}");
                return;
            }

            var target = request!;
            Socket upstreamSocket;
            string route;
            if (_passHostName)
            {
                // the socks5 mode: the name rides the CONNECT, the trusted upstream resolves
                upstreamSocket = await _socks.ConnectAsync(
                    target.Host, target.Port, TimeSpan.FromSeconds(_config.SocksTimeoutSeconds), _cts.Token);
                route = "upstream resolves";
            }
            else
            {
                var resolver = _resolver
                    ?? throw new InvalidOperationException("forward-proxy: no resolver configured for by-IP dialing");
                var destination = IPAddress.TryParse(target.Host, out var literal)
                    ? new ResolveResult(literal, "literal", null)
                    : await resolver.ResolveAsync(target.Host, _cts.Token);
                upstreamSocket = await _socks.ConnectAsync(
                    destination.Ip, target.Port, TimeSpan.FromSeconds(_config.SocksTimeoutSeconds), _cts.Token);
                route = $"{destination.Ip} ({destination.Source})";
            }

            using var upstreamClient = upstreamSocket;
            await stream.WriteAsync(EstablishResponse, _cts.Token);
            if (!target.IsProbe)
            {
                _marker.Touch();
            }

            _log.Debug($"forward-proxy: {target} -> {route}");

            // a peer that vanished without a FIN (a WSL distro shut down, a dropped link) would
            // otherwise leave the tunnel relaying nothing forever and counted as activity
            EnableKeepAlive(client.Client);
            EnableKeepAlive(upstreamClient);

            var upstreamStream = new NetworkStream(upstreamClient, ownsSocket: false);
            await TunnelAsync(stream, upstreamStream, stopToken);
        }
        catch (OperationCanceledException) when (!_running)
        {
            // service stop: close silently
        }
        catch (Exception ex)
        {
            // any failure of one connection (an unsupported address family, a bad certificate of a
            // DoH endpoint, a reset client) ends in a 502 and a counter, never in an unobserved fault
            Interlocked.Increment(ref _failedConnections);
            var detail = ex is OperationCanceledException
                ? "timeout"
                : ex.Message;
            _log.Error($"forward-proxy: {DescribeRemote(client)}: {detail}");
            try
            {
                await client.GetStream().WriteAsync(BadGatewayResponse, CancellationToken.None);
            }
            catch (Exception ex2) when (ex2 is IOException or InvalidOperationException or ObjectDisposedException or SocketException)
            {
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>The peer address for log lines; a reset client no longer has one.</summary>
    private static string DescribeRemote(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Pumps bytes in both directions; the end of one direction shuts down both sockets, and so
    /// does the cancellation of <paramref name="ct"/> (the service stop).
    /// </summary>
    private static async Task TunnelAsync(NetworkStream client, NetworkStream upstream, CancellationToken ct)
    {
        var up = PumpAsync(client, upstream, ct);
        var down = PumpAsync(upstream, client, ct);
        await Task.WhenAll(up, down);
    }

    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or NotSupportedException)
        {
            // keepalive is a safety net, not a requirement
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // the service stop: fall through to shutting both sockets down
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }

        try
        {
            if (to.Socket.Connected)
            {
                to.Socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (SocketException)
        {
        }
    }

    /// <summary>Reads the header up to \r\n\r\n with a size limit; null — the client left silently.</summary>
    private static async Task<byte[]?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (!ContainsHeaderEnd(buffer.GetBuffer(), (int)buffer.Length))
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > ConnectRequestParser.MaxHeaderBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static bool ContainsHeaderEnd(byte[] buffer, int length)
    {
        var window = buffer.AsSpan(0, length);
        return window.IndexOf("\r\n\r\n"u8) >= 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
