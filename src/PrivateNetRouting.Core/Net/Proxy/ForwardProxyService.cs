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

    /// <summary>
    /// False when the listener is held by another process (a detached worker): the counters above
    /// belong to this process's idle instance and say nothing about the traffic.
    /// </summary>
    bool CountersAreLocal => true;
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
/// all). It binds to the primary address from the config and on the WSL NAT gateway once the
/// adapter appears, following the gateway address if the WSL network is recreated. Normal mode — silence in the log: start/stop, warnings, errors;
/// per-request tracing — Debug.
/// </summary>
public sealed class ForwardProxyService : IForwardProxyInstance, IAsyncDisposable
{
    private static readonly byte[] EstablishResponse =
        "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

    private static readonly byte[] BadRequestResponse =
        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private static readonly byte[] ForbiddenResponse =
        "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private static readonly byte[] BadGatewayResponse =
        "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

    private readonly ForwardProxyConfig _config;
    private readonly IDnsResolver? _resolver;
    private readonly Socks5Client _socks;
    private readonly ActivityMarker _marker;
    private readonly FileLog _log;
    private readonly bool _passHostName;

    /// <summary>Per start: StopAsync cancels the token, and a fresh one is issued on the next
    /// StartAsync — a reused token would kill the accept loop the moment it starts. Everything
    /// that outlives the start call (accept loops, handlers, the WSL bind) works with the token
    /// of its own start, never re-reads this field.</summary>
    private CancellationTokenSource _cts = new();

    /// <summary>Serialises StartAsync/StopAsync/DisposeAsync: the token, the listener list and
    /// the running flag change together.</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
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
        // ConfigureAwait is mandatory here and in StopAsync (see there)
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var previous = _cts;
            _cts = cts;
            previous.Dispose(); // cancelled by its stop (or never used); handlers keep only its token

            try
            {
                var listen = Socks5Client.ParseEndPoint(_config.Listen);
                var listener = BindListener(listen, token)!;
                _ = AcceptLoopAsync(listener, token);
                _log.Info($"forward-proxy: listening on {listen}");
                if (!IPAddress.IsLoopback(listen.Address))
                {
                    // the proxy has no client authentication: whoever can reach this address can use the chain
                    _log.Warn($"forward-proxy: {listen} is not a loopback address — any host that can reach it can use the proxy and the chain behind it");
                }

                if (_config.BindWslGateway)
                {
                    _wslBindTask = Task.Run(() => WslBindWaitAsync(listen.Port, token), CancellationToken.None);
                }
            }
            catch
            {
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }

            _running = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        // ConfigureAwait is mandatory: the shutdown is awaited blocking (OnExit GUI) under the
        // Dispatcher context — a captured context would deadlock the wait
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running)
            {
                return;
            }

            _running = false;
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
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Bind on the WSL gateway and keep it that way: waits for the adapter with retries, retries a
    /// failed bind the same way and follows the address — a recreated WSL NAT network (wsl
    /// --shutdown, a Windows update, a network reset) gets a new gateway, the old listener is
    /// dropped and a new one bound — until the start this belongs to is stopped.
    /// </summary>
    private async Task WslBindWaitAsync(int port, CancellationToken token)
    {
        string? lastFailure = null;
        TcpListener? bound = null;
        IPAddress? boundAddress = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var gateway = WslGateway.FindIpv4();
                if (bound is not null && !Equals(gateway, boundAddress))
                {
                    _log.Warn(gateway is null
                        ? $"forward-proxy: WSL gateway {boundAddress} is gone, its listener is dropped"
                        : $"forward-proxy: WSL gateway moved from {boundAddress} to {gateway}, rebinding");
                    UnbindListener(bound);
                    bound = null;
                    boundAddress = null;
                }

                if (bound is null && gateway is not null)
                {
                    bound = BindListener(new IPEndPoint(gateway, port), token);
                    if (bound is null)
                    {
                        return; // the service was stopped meanwhile: nothing may be bound any more
                    }

                    boundAddress = gateway;
                    lastFailure = null;
                    _ = AcceptLoopAsync(bound, token);
                    _log.Info($"forward-proxy: listening on WSL gateway {gateway}");
                }
                else if (bound is null)
                {
                    _log.Debug("forward-proxy: WSL gateway adapter not found, retrying in 30 s");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // a failed bind or a failed adapter lookup is retried: the next attempt may succeed
                var failure = $"WSL gateway bind failed: {ex.Message}";
                if (failure == lastFailure)
                {
                    _log.Debug($"forward-proxy: {failure}, retrying in 30 s");
                }
                else
                {
                    _log.Warn($"forward-proxy: {failure}, retrying every 30 s");
                    lastFailure = failure;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Takes a listener of the WSL bind down; its accept loop ends on the stopped socket.</summary>
    private void UnbindListener(TcpListener listener)
    {
        lock (_gate)
        {
            _listeners.Remove(listener);
            listener.Stop();
        }
    }

    /// <summary>Null — the start this bind belongs to was stopped; a listener is never added after the stop cleared the list.</summary>
    private TcpListener? BindListener(IPEndPoint endpoint, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            var listener = new TcpListener(endpoint);
            listener.Start(backlog: 128);
            _listeners.Add(listener);
            return listener;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                // without capturing the caller's context: the accept loop must not depend
                // on the liveness of the UI thread (continuations — to the pool)
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // the listener was taken down by the service stop
                return;
            }
            catch (SocketException ex) when (token.IsCancellationRequested
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
                    await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    /// <summary>
    /// <paramref name="stopToken"/> is the token of the start that accepted this client: a stop
    /// must end its tunnels, and a later start must not hand them its fresh one.
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken stopToken)
    {
        using var _ = client;
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
        ConnectRequest? target = null;
        try
        {
            var stream = client.GetStream();

            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            headerCts.CancelAfter(TimeSpan.FromSeconds(_config.HeaderTimeoutSeconds));
            (byte[] Header, byte[] Leftover)? received;
            try
            {
                received = await ReadHeaderAsync(stream, headerCts.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException
                || (ex is OperationCanceledException && !stopToken.IsCancellationRequested))
            {
                // a client that never finished its request (a port scan, an idle pre-connect) or
                // left mid-way: not a proxy failure, nothing to answer
                _log.Debug($"forward-proxy: {DescribeRemote(client)}: no complete request ({(ex is OperationCanceledException ? "timeout" : ex.Message)})");
                return;
            }

            if (received is null)
            {
                return;
            }

            if (!ConnectRequestParser.TryParse(received.Value.Header, out var request, out var parseError))
            {
                await stream.WriteAsync(BadRequestResponse, stopToken);
                _log.Warn($"forward-proxy: {DescribeRemote(client)}: {parseError}");
                return;
            }

            target = request!;
            if (ConnectTargetPolicy.IsForbidden(target.Host))
            {
                await stream.WriteAsync(ForbiddenResponse, stopToken);
                _log.Warn($"forward-proxy: {DescribeRemote(client)} -> {target}: refused (a loopback or link-local target would reach the upstream's own host)");
                return;
            }

            Socket upstreamSocket;
            string route;
            if (_passHostName)
            {
                // the socks5 mode: the name rides the CONNECT, the trusted upstream resolves
                upstreamSocket = await _socks.ConnectAsync(
                    target.Host, target.Port, TimeSpan.FromSeconds(_config.SocksTimeoutSeconds), stopToken);
                route = "upstream resolves";
            }
            else
            {
                var resolver = _resolver
                    ?? throw new InvalidOperationException("forward-proxy: no resolver configured for by-IP dialing");
                var destination = IPAddress.TryParse(target.Host, out var literal)
                    ? new ResolveResult(literal, "literal", null)
                    : await resolver.ResolveAsync(target.Host, stopToken);

                // a name with several addresses: the first one that the upstream manages to dial
                IPAddress[] addresses = [destination.Ip, .. destination.Alternates ?? []];
                (upstreamSocket, var dialed) = await DialFirstAsync(addresses, target, stopToken);
                route = $"{dialed} ({destination.Source})";
            }

            using var upstreamClient = upstreamSocket;
            await stream.WriteAsync(EstablishResponse, stopToken);
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

            // bytes the client sent right behind the CONNECT header (a pipelined ClientHello)
            // belong to the tunnel: they were read together with the header
            var pipelined = received.Value.Leftover;
            if (pipelined.Length > 0)
            {
                await upstreamStream.WriteAsync(pipelined, stopToken);
            }

            await TunnelAsync(stream, upstreamStream, stopToken);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
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
            _log.Warn($"forward-proxy: {DescribeRemote(client)} -> {target?.ToString() ?? "(no target yet)"}: {detail}");
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

    /// <summary>Dials the addresses of one name in order; the last failure is the one that propagates.</summary>
    private async Task<(Socket Socket, IPAddress Address)> DialFirstAsync(
        IReadOnlyList<IPAddress> addresses, ConnectRequest target, CancellationToken stopToken)
    {
        var timeout = TimeSpan.FromSeconds(_config.SocksTimeoutSeconds);
        for (var i = 0; ; i++)
        {
            try
            {
                return (await _socks.ConnectAsync(addresses[i], target.Port, timeout, stopToken), addresses[i]);
            }
            catch (Exception) when (i < addresses.Count - 1 && !stopToken.IsCancellationRequested)
            {
                _log.Debug($"forward-proxy: {target}: dialing {addresses[i]} failed, trying the next address");
            }
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
    /// Pumps bytes in both directions until both have ended. A clean end of one direction is a
    /// half-close: only the sending side of the peer is shut down, the other direction keeps
    /// flowing. A failure or the cancellation of <paramref name="ct"/> (the service stop) shuts
    /// both sockets down so that the opposite pump ends too.
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
        var cleanEnd = false;
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    cleanEnd = true;
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
                to.Socket.Shutdown(cleanEnd ? SocketShutdown.Send : SocketShutdown.Both);
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Reads the header up to \r\n\r\n with a size limit and returns it together with the bytes
    /// that arrived behind it in the same reads; null — the client left silently or the header is too long.
    /// </summary>
    private static async Task<(byte[] Header, byte[] Leftover)?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            var length = (int)buffer.Length;
            var end = buffer.GetBuffer().AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var headerLength = end + 4;
                if (headerLength > ConnectRequestParser.MaxHeaderBytes)
                {
                    return null;
                }

                var all = buffer.GetBuffer();
                return (all[..headerLength], all[headerLength..length]);
            }

            if (length > ConnectRequestParser.MaxHeaderBytes)
            {
                return null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        // under the lifecycle lock: a start racing the dispose must not see a disposed source;
        // handlers still alive hold only the (already cancelled) token, which stays usable
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _cts.Dispose();
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
